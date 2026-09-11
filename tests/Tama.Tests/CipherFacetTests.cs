using Tama.Core.Contracts;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 分面筛选（CipherSearchRequest 的多栏条件 + CipherFacets 计数）。
///
/// 盯住三件事，都是筛选栏能不能用的前提：
///  1. **同栏并集、跨栏交集**——选了文件夹再叠 TOTP，两个条件都得满足；
///  2. **分面计数要排除自己那一栏**——选了文件夹 A 之后，别的文件夹的计数不能被"只看 A"锁成 0，
///     否则一选就切不动、也叠不了（这是最容易写错、也最恶心的一条）；
///  3. 查不到的可选值计数为 0，UI 才有依据把它灰掉。
/// </summary>
public class CipherFacetTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TamaDbContext _dbSeed, _db;
    private readonly CipherService _svc;

    private readonly Guid _work = Guid.NewGuid(), _personal = Guid.NewGuid();
    private readonly Guid _bankId = Guid.NewGuid(), _noteId = Guid.NewGuid(), _visaId = Guid.NewGuid();

    public CipherFacetTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(_conn).Options;

        _dbSeed = new TamaDbContext(options);
        _dbSeed.Database.EnsureCreated();
        _dbSeed.Folders.Add(new Folder { Id = _work, Name = "Work" });
        _dbSeed.Folders.Add(new Folder { Id = _personal, Name = "Personal" });

        // c1：登录 + Work + 有 TOTP
        _dbSeed.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(), Type = CipherType.Login, Name = "GitHub", FolderId = _work,
            Login = new CipherLogin { Username = "octocat", Password = "p", Totp = "JBSWY3DPEHPK3PXP" },
        });
        // c2：登录 + Personal + 有通行密钥（Fido2 凭据）
        // 这里刻意走真实的 EF 写入路径：Id 由写入方编号（复合主键 (CipherId, Id)），
        // 模型一旦回退成 store-generated，这一行就会以
        // "NOT NULL constraint failed: Fido2Credential.Id" 炸掉。
        _dbSeed.Ciphers.Add(new Cipher
        {
            Id = _bankId, Type = CipherType.Login, Name = "Bank", FolderId = _personal,
            Login = new CipherLogin { Username = "me" },
            Fido2Credentials = new List<Fido2Credential>
            {
                new() { Id = 0, CredentialId = "cred-1", RpId = "bank.example", KeyValue = "k" },
            },
        });
        // c3：笔记 + 未分类
        _dbSeed.Ciphers.Add(new Cipher
        {
            Id = _noteId, Type = CipherType.SecureNote, Name = "Note", FolderId = null,
            SecureNote = new CipherNote { Text = "hello" },
        });
        // c4：银行卡 + Personal
        _dbSeed.Ciphers.Add(new Cipher
        {
            Id = _visaId, Type = CipherType.Card, Name = "Visa", FolderId = _personal,
            Card = new CipherCard { CardholderName = "ME", Number = "4111" },
        });
        // c5：已删除，任何时候都不该出现
        _dbSeed.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(), Type = CipherType.Login, Name = "Gone",
            DeletedAt = DateTime.UtcNow, Login = new CipherLogin { Totp = "AAAA" },
        });
        _dbSeed.SaveChanges();

        // 查询用另一个上下文：避免被种子数据的跟踪状态影响
        _db = new TamaDbContext(options);
        _svc = new CipherService(_db, null!, null!);
    }

    private static int TypeCount(CipherFacets f, int type) => f.Types.Single(t => t.Type == type).Count;
    private static int FolderCount(CipherFacets f, string name) => f.Folders.Single(x => x.Name == name).Count;

    [Fact]
    public async Task Unfiltered_Returns_Everything_With_Facet_Counts()
    {
        var r = await _svc.Search(new CipherSearchRequest());

        Assert.Equal(4, r.Ciphers.Count);               // c5 已删除，不算
        Assert.Equal(4, r.Facets.Total);

        Assert.Equal(2, TypeCount(r.Facets, 1));        // 登录：c1 c2
        Assert.Equal(1, TypeCount(r.Facets, 2));        // 笔记：c3
        Assert.Equal(1, TypeCount(r.Facets, 3));        // 银行卡：c4
        Assert.Equal(0, TypeCount(r.Facets, 4));        // 身份：没有 → UI 灰掉

        Assert.Equal(1, FolderCount(r.Facets, "未分类"));
        Assert.Equal(1, FolderCount(r.Facets, "Work"));
        Assert.Equal(2, FolderCount(r.Facets, "Personal"));

        Assert.Equal(1, r.Facets.TotpCount);
        Assert.Equal(1, r.Facets.PasskeyCount);
    }

    /// <summary>本类里最要紧的一条：文件夹栏的计数不能被"已选文件夹"自己砍掉</summary>
    [Fact]
    public async Task Folder_Facet_Ignores_Its_Own_Column()
    {
        var r = await _svc.Search(new CipherSearchRequest { FolderIds = new List<string> { _personal.ToString() } });

        Assert.Equal(2, r.Ciphers.Count);                       // c2 c4
        Assert.Equal(2, FolderCount(r.Facets, "Personal"));     // 仍是 2，不是 0
        Assert.Equal(1, FolderCount(r.Facets, "Work"));         // 还能切过去
        Assert.Equal(1, FolderCount(r.Facets, "未分类"));
    }

    /// <summary>
    /// 跨栏取交集：Personal + TOTP → 一条都没有。
    /// 此时**所有**未选中的选项都该灰掉：c1 在 Work（被文件夹挡）、c2 没 TOTP、
    /// c3 未分类、c4 没 TOTP 也没通行密钥 —— 这正是"查不到就禁止选择"该有的样子。
    /// </summary>
    [Fact]
    public async Task Cross_Facet_Is_Intersection_And_Zero_Means_Greyed()
    {
        var r = await _svc.Search(new CipherSearchRequest
        {
            FolderIds = new List<string> { _personal.ToString() },
            HasTotp = true,
        });

        Assert.Empty(r.Ciphers);
        Assert.Equal(0, r.Facets.TotpCount);        // 已选中 → 不禁用，但计数如实为 0
        Assert.Equal(0, r.Facets.PasskeyCount);     // c2 有通行密钥但没有 TOTP → 也查不到
        Assert.Equal(0, TypeCount(r.Facets, 1));
        Assert.Equal(0, TypeCount(r.Facets, 2));
        Assert.Equal(0, TypeCount(r.Facets, 3));
        Assert.Equal(0, TypeCount(r.Facets, 4));
        // 文件夹栏只排除"文件夹"自己那一栏的条件，属性栏的条件**要**算进来：
        // 带 TOTP 的条目只有 Work 里的 c1，所以另外两个文件夹的计数就是 0（该灰）
        Assert.Equal(1, FolderCount(r.Facets, "Work"));
        Assert.Equal(0, FolderCount(r.Facets, "Personal"));
        Assert.Equal(0, FolderCount(r.Facets, "未分类"));
    }

    [Fact]
    public async Task Totp_And_Passkey_Toggles_Intersect()
    {
        var r = await _svc.Search(new CipherSearchRequest { HasTotp = true, HasPasskey = true });

        Assert.Empty(r.Ciphers);                    // 没有同时具备两者的条目
        // 两个开关互为"另一栏"：各自都要把对方的条件算进去，于是都算不出东西。
        // 但两枚 chip 都已选中，UI 不会禁用它们——否则用户就取消不掉了。
        Assert.Equal(0, r.Facets.TotpCount);
        Assert.Equal(0, r.Facets.PasskeyCount);
    }

    [Fact]
    public async Task Same_Facet_Is_Union()
    {
        // Work ∪ Personal（同栏两个文件夹）→ c1 c2 c4
        var r = await _svc.Search(new CipherSearchRequest
        {
            FolderIds = new List<string> { _work.ToString(), _personal.ToString() },
        });

        Assert.Equal(3, r.Ciphers.Count);

        // 类型栏同理：登录 ∪ 银行卡
        var r2 = await _svc.Search(new CipherSearchRequest { Types = new List<int> { 1, 3 } });
        Assert.Equal(3, r2.Ciphers.Count);
    }

    [Fact]
    public async Task Folder_Plus_Totp_Intersects()
    {
        var hit = await _svc.Search(new CipherSearchRequest
        {
            FolderIds = new List<string> { _work.ToString() },
            HasTotp = true,
        });
        Assert.Single(hit.Ciphers);
        Assert.Equal("GitHub", hit.Ciphers[0].Name);

        var miss = await _svc.Search(new CipherSearchRequest { HasTotp = true, HasPasskey = true });
        Assert.Empty(miss.Ciphers);
    }

    [Fact]
    public async Task Unfiled_Sentinel_Selects_Ciphers_Without_Folder()
    {
        // 空字符串 = 未分类（FolderId == null），与"不限"（null/空集合）区分开
        var r = await _svc.Search(new CipherSearchRequest { FolderIds = new List<string> { "" } });

        Assert.Single(r.Ciphers);
        Assert.Equal("Note", r.Ciphers[0].Name);
    }

    [Fact]
    public async Task Passkey_Facet_Uses_Fido2_Credentials()
    {
        var r = await _svc.Search(new CipherSearchRequest { HasPasskey = true });

        Assert.Single(r.Ciphers);
        Assert.Equal("Bank", r.Ciphers[0].Name);
        // 选了通行密钥之后，文件夹栏仍应保留其它文件夹的可达计数
        Assert.Equal(1, FolderCount(r.Facets, "Personal"));
        Assert.Equal(0, TypeCount(r.Facets, 2));   // 笔记里没有带通行密钥的 → 灰
    }

    [Fact]
    public async Task Query_Narrows_The_Facet_Denominator()
    {
        // 文本查询是分面的分母：搜 "visa" 之后 Total 只剩 1，其它栏的计数都跟着收敛
        var r = await _svc.Search(new CipherSearchRequest { Query = "visa" });

        Assert.Single(r.Ciphers);
        Assert.Equal(1, r.Facets.Total);
        Assert.Equal(1, TypeCount(r.Facets, 3));
        Assert.Equal(0, TypeCount(r.Facets, 1));
        Assert.Equal(1, FolderCount(r.Facets, "Personal"));
        Assert.Equal(0, FolderCount(r.Facets, "Work"));
        Assert.Equal(0, r.Facets.TotpCount);
        Assert.Equal(0, r.Facets.PasskeyCount);
    }

    /// <summary>
    /// 组织条目的 folderId 可能指向 **collection**，而 sync 响应的 folders 只有个人文件夹 →
    /// 本地会出现"指向未知文件夹"的悬空 Id。UI（CipherUi.FolderName）早就把这种显示成"未分类"，
    /// 计数必须跟它一致，否则筛选栏各项相加 ≠ 总数（实测差 1，就是这么发现的）。
    /// </summary>
    [Fact]
    public async Task Cipher_With_Unknown_Folder_Counts_As_Unfiled()
    {
        var dangling = Guid.NewGuid();
        _dbSeed.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(), Type = CipherType.Login, Name = "组织条目", FolderId = dangling,
            Login = new CipherLogin { Username = "u" },
        });
        _dbSeed.SaveChanges();

        var all = await _svc.Search(new CipherSearchRequest());

        // 不变式：文件夹栏各项相加 = 总条数（悬空 Id 曾经两边都不算）
        Assert.Equal(all.Ciphers.Count, all.Facets.Folders.Sum(f => f.Count));
        Assert.Equal(2, FolderCount(all.Facets, "未分类"));   // c3（未分类）+ 这条悬空的

        // 点"未分类"也要能筛到它
        var unfiled = await _svc.Search(new CipherSearchRequest { FolderIds = new List<string> { "" } });
        Assert.Contains(unfiled.Ciphers, c => c.Name == "组织条目");
    }

    public void Dispose()
    {
        _dbSeed.Dispose();
        _db.Dispose();
        _conn.Dispose();
    }
}
