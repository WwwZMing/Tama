using Tama.Core.Contracts;
using Tama.Core.Exceptions;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Auth;
using Tama.Services.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 增删改查全链路（对着假 Bitwarden 客户端，**绝不碰真实账号**）。
///
/// 数据是刻意"凑全"的：四种类型各一条，带自定义字段 / TOTP / 多网址 / 备注 / 收藏 / 文件夹，
/// 因为只测 Login 的 CRUD 正是之前那批 bug 能藏这么久的原因。
/// 每个写操作都会把**上行请求体**解密回来断言——"本地对但推上去是残的"只有这样才能抓。
/// </summary>
public class CipherCrudTests : IDisposable
{
    // 三个独立密钥：DbKey 解账号敏感字段，Enc/Mac 是账号派生出来的词条密钥
    private static readonly byte[] DbKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] EncKey = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] MacKey = Enumerable.Range(64, 32).Select(i => (byte)i).ToArray();

    private readonly SqliteConnection _vaultConn, _authConn;
    private readonly TamaDbContext _vault;
    private readonly AuthDbContext _auth;
    private readonly FakeBitwardenClient _client = new();
    private readonly Guid _folderId = Guid.NewGuid();
    private CipherService? _svc;

    public CipherCrudTests()
    {
        _vaultConn = new SqliteConnection("DataSource=:memory:");
        _vaultConn.Open();
        _authConn = new SqliteConnection("DataSource=:memory:");
        _authConn.Open();

        _vault = new TamaDbContext(new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(_vaultConn).Options);
        _auth = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_authConn).Options);
        _vault.Database.EnsureCreated();
        _auth.Database.EnsureCreated();

        _vault.Folders.Add(new Folder { Id = _folderId, Name = "工作" });
        _vault.SaveChanges();
    }

    /// <summary>造一个"已关联 Bitwarden 账号"的状态；linked=false 则是纯本地库</summary>
    private CipherService Service(bool linked)
    {
        var keyService = new DatabaseKeyService();
        keyService.SetKey(DbKey);

        if (linked)
        {
            var account = new AccountData
            {
                Id = "acc-1",
                Email = "me@example.com",
                Type = "bitwarden",
                ServerUrl = "https://vault.bitwarden.com",
                AccessToken = "access-token",
                DerivedEncKey = Convert.ToBase64String(EncKey),
                DerivedMacKey = Convert.ToBase64String(MacKey),
                EncryptionKey = "user-key",
            };
            account.EncryptSensitiveFields(DbKey);   // 真实路径就是这么存的（DecryptSensitiveFields 认得 enc: 前缀）
            _auth.Accounts.Add(account);
            _auth.SaveChanges();
        }

        return _svc = new CipherService(_vault, new BitwardenAccountService(_auth, keyService, _client), _client);
    }

    // ───────────────────────── 凑几条"全"的数据 ─────────────────────────

    private Cipher SeedFullLogin(string name = "GitHub", string syncStatus = "synced")
    {
        var c = new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Login,
            Name = name,
            Notes = "恢复码在保险箱",
            Favorite = true,
            FolderId = _folderId,
            SyncStatus = syncStatus,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Login = new CipherLogin
            {
                Username = "octocat",
                Password = "s3cret-pw",
                Totp = "JBSWY3DPEHPK3PXP",
                Uris = new() { "https://github.com", "https://gist.github.com" },
            },
            Fields = new()
            {
                new CipherField { Id = 0, Name = "PIN", Value = "1234", Type = 1, Hidden = true },
                new CipherField { Id = 1, Name = "备注字段", Value = "hello", Type = 0 },
            },
        };
        _vault.Ciphers.Add(c);
        _vault.SaveChanges();
        return c;
    }

    private Cipher SeedFullCard(string name = "Visa")
    {
        var c = new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Card,
            Name = name,
            SyncStatus = "synced",
            Card = new CipherCard
            {
                CardholderName = "ZHANG SAN",
                Number = "4111111111111111",
                Brand = "Visa",
                ExpMonth = "07",
                ExpYear = "2030",
                Code = "123",
            },
        };
        _vault.Ciphers.Add(c);
        _vault.SaveChanges();
        return c;
    }

    private Cipher SeedFullNote(string name = "WiFi 密码")
    {
        var c = new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.SecureNote,
            Name = name,
            Notes = "密码是 hunter2",
            SyncStatus = "synced",
            SecureNote = new CipherNote(),
        };
        _vault.Ciphers.Add(c);
        _vault.SaveChanges();
        return c;
    }

    private Cipher SeedFullIdentity(string name = "我的身份")
    {
        var c = new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Identity,
            Name = name,
            SyncStatus = "synced",
            Identity = new CipherIdentity
            {
                FirstName = "San",
                LastName = "Zhang",
                Email = "san@example.com",
                Phone = "13800000000",
                Ssn = "110101199001011234",
                Username = "san",
                Address = "北京市朝阳区某路 1 号",
            },
        };
        _vault.Ciphers.Add(c);
        _vault.SaveChanges();
        return c;
    }

    // ═══════════════════════════ 查 ═══════════════════════════

    [Fact]
    public async Task Read_Returns_All_Four_Types_With_Their_Own_Fields()
    {
        var login = SeedFullLogin();
        var card = SeedFullCard();
        var note = SeedFullNote();
        var identity = SeedFullIdentity();
        var svc = Service(linked: false);

        var r = await svc.Search(new CipherSearchRequest());
        Assert.Equal(4, r.Ciphers.Count);
        // 类型栏各一条、且文件夹栏也对得上
        Assert.Equal(1, r.Facets.Types.Single(t => t.Type == 1).Count);
        Assert.Equal(1, r.Facets.Types.Single(t => t.Type == 2).Count);
        Assert.Equal(1, r.Facets.Types.Single(t => t.Type == 3).Count);
        Assert.Equal(1, r.Facets.Types.Single(t => t.Type == 4).Count);
        Assert.Equal(1, r.Facets.TotpCount);

        // Get 单条也要带全各自的字段
        var l = await svc.Get(login.Id.ToString());
        Assert.Equal("octocat", l!.Login!.Username);
        Assert.Equal("JBSWY3DPEHPK3PXP", l.Login.Totp);
        Assert.Equal(2, l.Login.Uris!.Count);
        Assert.Equal("恢复码在保险箱", l.Notes);
        Assert.True(l.Favorite);

        // ⚠ 卡片/身份只能从实体读：CipherDto（页面拿到的那份）目前只有 Login 和 Notes，
        //   这正是"拉下来的卡片在界面上看不到"的根源（UI 那层还没做，见 CLAUDE.md）。
        var c = await _vault.Ciphers.AsNoTracking().SingleAsync(x => x.Id == card.Id);
        Assert.Equal("4111111111111111", c.Card!.Number);
        Assert.Equal("2030", c.Card.ExpYear);

        var n = await svc.Get(note.Id.ToString());
        Assert.Equal("密码是 hunter2", n!.Notes);

        var i = await _vault.Ciphers.AsNoTracking().SingleAsync(x => x.Id == identity.Id);
        Assert.Equal("北京市朝阳区某路 1 号", i.Identity!.Address);
        Assert.Equal("13800000000", i.Identity.Phone);
    }

    // ═══════════════════════════ 增 ═══════════════════════════

    [Fact]
    public async Task Create_Without_Account_Stays_Local_Pending()
    {
        var svc = Service(linked: false);

        var created = await svc.Create(new CreateCipherRequest
        {
            Type = 1,
            Name = "新站点",
            Notes = "备注",
            Login = new CipherLoginRequest
            {
                Username = "u",
                Password = "p",
                Totp = "JBSWY3DPEHPK3PXP",
                Uris = new() { "https://new.example" },
            },
        });

        Assert.Equal("pending", created.SyncStatus);
        Assert.Empty(_client.CreatedBodies);           // 纯本地库：一次上行都没有

        var local = await svc.Get(created.Id);
        Assert.NotNull(local);
        Assert.Equal("pending", local!.SyncStatus);
        Assert.Equal("u", local.Login!.Username);
        Assert.Equal("JBSWY3DPEHPK3PXP", local.Login.Totp);
    }

    /// <summary>
    /// 在线新建：推送成功 → 回写服务器 ID、状态变 synced，而且**推上去的内容是完整的**
    /// （用户名/密码/TOTP/多网址/备注一个都不能少）。
    /// </summary>
    [Fact]
    public async Task Create_With_Account_Pushes_Complete_Body_And_Remaps_Id()
    {
        var svc = Service(linked: true);
        var serverId = Guid.NewGuid().ToString();
        _client.CreateReturnsId = serverId;

        var created = await svc.Create(new CreateCipherRequest
        {
            Type = 1,
            Name = "新站点",
            Notes = "备注",
            Login = new CipherLoginRequest
            {
                Username = "u",
                Password = "p",
                Totp = "JBSWY3DPEHPK3PXP",
                Uris = new() { "https://new.example", "https://alt.example" },
            },
        });

        Assert.Equal("synced", created.SyncStatus);
        Assert.Equal(serverId, created.Id);                       // 回写成服务器 ID
        var body = Assert.Single(_client.CreatedBodies);

        var onServer = FakeBitwardenClient.DecodeBody(body, EncKey, MacKey);
        Assert.Equal(CipherType.Login, onServer.Type);
        Assert.Equal("新站点", onServer.Name);
        Assert.Equal("备注", onServer.Notes);
        Assert.Equal("u", onServer.Login!.Username);
        Assert.Equal("p", onServer.Login.Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", onServer.Login.Totp);
        Assert.Equal(new[] { "https://new.example", "https://alt.example" }, onServer.Login.Uris!.ToArray());

        // 本地那份也应该是 synced（且行能被 Get 到）
        var local = await svc.Get(serverId);
        Assert.Equal("synced", local!.SyncStatus);
    }

    [Fact]
    public async Task Create_SecureNote_Pushes_Notes_Body()
    {
        var svc = Service(linked: true);

        await svc.Create(new CreateCipherRequest { Type = 2, Name = "WiFi", Notes = "密码是 hunter2" });

        var onServer = FakeBitwardenClient.DecodeBody(Assert.Single(_client.CreatedBodies), EncKey, MacKey);
        Assert.Equal(CipherType.SecureNote, onServer.Type);
        Assert.Equal("密码是 hunter2", onServer.Notes);
        Assert.NotNull(onServer.SecureNote);      // secureNote.type 得带上，否则服务器不认这是笔记
    }

    [Fact]
    public async Task Create_Push_Failure_Keeps_Pending()
    {
        var svc = Service(linked: true);
        _client.CreateReturnsId = null;           // 推送失败

        var created = await svc.Create(new CreateCipherRequest { Type = 1, Name = "推不上去" });

        Assert.Equal("pending", created.SyncStatus);
        var local = await svc.Get(created.Id);
        Assert.Equal("pending", local!.SyncStatus);
        Assert.Equal("create", (await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == Guid.Parse(created.Id))).PendingOp);
    }

    // ═══════════════════════════ 改 ═══════════════════════════

    /// <summary>
    /// 这条是"收藏一张卡片会把卡片推空"那个事故的直接回归：
    /// 改的只是名字，但推上去的整条对象里卡片字段必须还在。
    /// </summary>
    [Fact]
    public async Task Update_Pushes_Complete_Object_Including_Other_Sections()
    {
        var card = SeedFullCard();
        var svc = Service(linked: true);

        await svc.Update(new UpdateCipherRequest { Id = card.Id.ToString(), Type = 3, Name = "Visa 主卡" });

        Assert.Equal("synced", (await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == card.Id)).SyncStatus);

        var (id, body) = Assert.Single(_client.UpdatedBodies);
        Assert.Equal(card.Id.ToString(), id);
        var onServer = FakeBitwardenClient.DecodeBody(body, EncKey, MacKey);
        Assert.Equal("Visa 主卡", onServer.Name);
        Assert.NotNull(onServer.Card);
        Assert.Equal("4111111111111111", onServer.Card!.Number);
        Assert.Equal("ZHANG SAN", onServer.Card.CardholderName);
        Assert.Equal("123", onServer.Card.Code);
    }

    [Fact]
    public async Task Update_Login_Keeps_Custom_Fields_And_Totp()
    {
        var login = SeedFullLogin();
        var svc = Service(linked: true);

        await svc.Update(new UpdateCipherRequest
        {
            Id = login.Id.ToString(),
            Type = 1,
            Name = "GitHub（改名）",
            Notes = login.Notes,
            Favorite = login.Favorite,
            FolderId = _folderId.ToString(),
            Login = new CipherLoginRequest
            {
                Username = "octocat",
                Password = "s3cret-pw",
                Totp = "JBSWY3DPEHPK3PXP",
                Uris = new() { "https://github.com", "https://gist.github.com" },
            },
        });

        var onServer = FakeBitwardenClient.DecodeBody(Assert.Single(_client.UpdatedBodies).Body, EncKey, MacKey);
        Assert.Equal("GitHub（改名）", onServer.Name);
        Assert.Equal("JBSWY3DPEHPK3PXP", onServer.Login!.Totp);
        Assert.Equal(2, onServer.Login.Uris!.Count);
        // 自定义字段是从本地实体带出去，不经过 UI 的 DTO——所以"改个名字"也不会丢
        Assert.Equal(2, onServer.Fields!.Count);
        Assert.Equal("PIN", onServer.Fields[0].Name);
        Assert.True(onServer.Fields[0].Hidden);
        Assert.Equal(_folderId, onServer.FolderId);
    }

    [Fact]
    public async Task Update_Without_Account_Keeps_Pending_And_Never_Pushes()
    {
        var card = SeedFullCard();
        var svc = Service(linked: false);

        await svc.Update(new UpdateCipherRequest { Id = card.Id.ToString(), Type = 3, Name = "改过" });

        Assert.Empty(_client.UpdatedBodies);
        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == card.Id);
        Assert.Equal("pending", after.SyncStatus);
        Assert.Equal("update", after.PendingOp);
        Assert.Equal("4111111111111111", after.Card!.Number);   // 本地改名字不该动卡片字段
    }

    [Fact]
    public async Task SetFavorite_Online_Pushes_Card_Untouched()
    {
        var card = SeedFullCard();
        var svc = Service(linked: true);

        await svc.SetFavorite(card.Id.ToString(), true);

        var onServer = FakeBitwardenClient.DecodeBody(Assert.Single(_client.UpdatedBodies).Body, EncKey, MacKey);
        Assert.True(onServer.Favorite);
        Assert.Equal("4111111111111111", onServer.Card!.Number);   // 收藏绝不能把卡片推空
        Assert.Equal(CipherType.Card, onServer.Type);
    }

    [Fact]
    public async Task Update_Push_Failure_Falls_Back_To_Pending()
    {
        var card = SeedFullCard();
        var svc = Service(linked: true);
        _client.UpdateSucceeds = false;

        await svc.Update(new UpdateCipherRequest { Id = card.Id.ToString(), Type = 3, Name = "推不上去" });

        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == card.Id);
        Assert.Equal("pending", after.SyncStatus);
        Assert.Equal("update", after.PendingOp);
        Assert.Equal("推不上去", after.Name);      // 本地已经改了，等 worker 重试
        Assert.Equal(0, after.RetryCount);         // 用户这次编辑重置了重试预算
    }

    // ═══════════════════════════ 删 ═══════════════════════════

    [Fact]
    public async Task Delete_Synced_Cipher_Pushes_Delete_And_Removes_Locally()
    {
        var login = SeedFullLogin();
        var svc = Service(linked: true);

        await svc.Delete(login.Id.ToString());

        Assert.Equal(login.Id.ToString(), Assert.Single(_client.DeletedIds));
        Assert.False(await _vault.Ciphers.AnyAsync(c => c.Id == login.Id));
    }

    [Fact]
    public async Task Delete_Never_Pushed_Cipher_Does_Not_Touch_Server()
    {
        var svc = Service(linked: true);
        var created = await svc.Create(new CreateCipherRequest { Type = 1, Name = "离线新建" });
        _client.CreateReturnsId = null;
        // 上面那次 create 已经推送过（CreateReturnsId 默认给了 id），这里另造一条纯本地的
        var localOnly = new Cipher
        {
            Id = Guid.NewGuid(), Type = CipherType.Login, Name = "纯本地",
            SyncStatus = "pending", PendingOp = "create",
            Login = new CipherLogin { Username = "u" },
        };
        _vault.Ciphers.Add(localOnly);
        await _vault.SaveChangesAsync();

        await svc.Delete(localOnly.Id.ToString());

        Assert.Empty(_client.DeletedIds);
        Assert.Empty(_client.TrashedIds);
        Assert.False(await _vault.Ciphers.AnyAsync(c => c.Id == localOnly.Id));
        Assert.True(await _vault.Ciphers.AnyAsync(c => c.Id == Guid.Parse(created.Id)));   // 别误删别的
    }

    [Fact]
    public async Task Delete_SoftDelete_Uses_Trash_And_Keeps_Row()
    {
        var login = SeedFullLogin();
        var svc = Service(linked: true);

        await svc.Delete(login.Id.ToString(), softDelete: true);

        Assert.Equal(login.Id.ToString(), Assert.Single(_client.TrashedIds));
        Assert.Empty(_client.DeletedIds);
        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == login.Id);
        Assert.NotNull(after.DeletedAt);            // 回收站语义：行还在，只是标记删除
        Assert.Equal("synced", after.SyncStatus);
    }

    [Fact]
    public async Task Delete_Unknown_Id_Throws()
    {
        var svc = Service(linked: true);
        await Assert.ThrowsAsync<TamaException>(() => svc.Delete(Guid.NewGuid().ToString()));
    }

    public void Dispose()
    {
        _vault.Dispose();
        _auth.Dispose();
        _vaultConn.Dispose();
        _authConn.Dispose();
        GC.SuppressFinalize(this);
    }
}
