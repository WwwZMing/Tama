using System.Text.Json;
using Tama.Core.Formats;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Auth;
using Tama.Services.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 「导出给 Bitwarden」的格式回归。
///
/// 这组测试的价值全在**几个从 bitwarden/clients 源码里读出来、光看文档一定会踩的坑**上：
///   1. <c>encrypted</c> 必须是字面量 false —— 导入器见到加密的直接抛异常；
///   2. 自定义字段只认 <c>type</c>，**没有 hidden 字段** —— 写错的话用户标了"隐藏"的字段
///      会在 Bitwarden 里变成明文展示，而且导入不报错；
///   3. 安全笔记必须带 <c>secureNote: {type: 0}</c>，否则 Bitwarden 不认它是笔记。
///
/// 断言刻意打在**序列化之后的 JSON 上**（而不是 C# 对象上）：
/// 属性名拼错、camelCase 策略丢失这类问题，只有看真实字节才能发现。
/// </summary>
public class BitwardenExportTests : IDisposable
{
    private static readonly byte[] DbKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly SqliteConnection _vaultConn, _authConn;
    private readonly TamaDbContext _vault;
    private readonly AuthDbContext _auth;
    private readonly FakeBitwardenClient _client = new();
    private readonly Guid _folderId = Guid.NewGuid();

    public BitwardenExportTests()
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

    private CipherService Service()
    {
        var keys = new DatabaseKeyService();
        keys.SetKey(DbKey);
        return new CipherService(_vault, new BitwardenAccountService(_auth, keys, _client), _client);
    }

    /// <summary>走真实的序列化设置产出字节——这是用户真正交给 Bitwarden 的那份东西。</summary>
    private JsonDocument ExportJson(out BitwardenVaultFile file)
    {
        file = Service().BuildBitwardenExport();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, BitwardenVaultJson.WriteOptions);
        return JsonDocument.Parse(bytes);
    }

    // ───────────────────────── 造数据 ─────────────────────────

    private void SeedAllFourTypes()
    {
        _vault.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Login,
            Name = "GitHub",
            Notes = "恢复码在保险箱",
            Favorite = true,
            FolderId = _folderId,
            SyncStatus = "synced",
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
                new CipherField { Id = 1, Name = "备注", Value = "hello", Type = 0, Hidden = false },
                // 这个是最阴的：Type 是默认 0（文本）但 Hidden=true。
                // 只认 Type 的话它会在 Bitwarden 里变明文。
                new CipherField { Id = 2, Name = "兜底隐藏", Value = "shh", Type = 0, Hidden = true },
            },
        });
        _vault.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(), Type = CipherType.Card, Name = "Visa", SyncStatus = "synced",
            Card = new CipherCard
            {
                CardholderName = "ZHANG SAN", Number = "4111111111111111", Brand = "Visa",
                ExpMonth = "07", ExpYear = "2030", Code = "123",
            },
        });
        _vault.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(), Type = CipherType.SecureNote, Name = "WiFi 密码",
            Notes = "密码是 hunter2", SyncStatus = "synced", SecureNote = new CipherNote(),
        });
        _vault.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(), Type = CipherType.Identity, Name = "我的身份", SyncStatus = "synced",
            Identity = new CipherIdentity
            {
                FirstName = "San", LastName = "Zhang", Email = "san@example.com",
                Phone = "13800000000", Ssn = "110101199001011234", Username = "san",
                Address = "北京市朝阳区某路 1 号",
            },
        });
        _vault.SaveChanges();
    }

    // ═══════════════════════ 信封 ═══════════════════════

    [Fact]
    public void Envelope_Is_Unencrypted_With_Folders_And_Items()
    {
        SeedAllFourTypes();
        using var doc = ExportJson(out var file);
        var root = doc.RootElement;

        // 坑 1：导入器 isUnencrypted 不通过就直接抛
        Assert.False(root.GetProperty("encrypted").GetBoolean());
        Assert.Equal(4, root.GetProperty("items").GetArrayLength());
        Assert.Equal(1, root.GetProperty("folders").GetArrayLength());
        Assert.Equal("工作", root.GetProperty("folders")[0].GetProperty("name").GetString());
        Assert.Equal(4, file.Items.Count);
    }

    [Fact]
    public void Every_Item_Carries_The_Required_Bitwarden_Keys()
    {
        SeedAllFourTypes();
        using var doc = ExportJson(out _);

        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            // CipherExport 里 type/name/reprompt 是必填（reprompt 有默认值，缺了会被当 0，
            // 但显式给出才是 Bitwarden 自己导出的样子）
            Assert.True(item.TryGetProperty("type", out var type));
            Assert.Equal(JsonValueKind.Number, type.ValueKind);   // 枚举值，不是字符串
            Assert.True(item.TryGetProperty("name", out _));
            Assert.Equal(0, item.GetProperty("reprompt").GetInt32());
            Assert.False(item.GetProperty("favorite").ValueKind == JsonValueKind.Undefined);
        }
    }

    // ═══════════════════════ 四种类型 ═══════════════════════

    [Fact]
    public void All_Four_Types_Map_To_Bitwarden_CipherTypes_With_Their_Own_Section()
    {
        SeedAllFourTypes();
        using var doc = ExportJson(out _);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();

        // Bitwarden CipherType：1=Login 2=SecureNote 3=Card 4=Identity（与本地枚举同值）
        var login = items.Single(i => i.GetProperty("type").GetInt32() == 1);
        Assert.Equal("GitHub", login.GetProperty("name").GetString());
        Assert.Equal("恢复码在保险箱", login.GetProperty("notes").GetString());
        Assert.True(login.GetProperty("favorite").GetBoolean());
        var l = login.GetProperty("login");
        Assert.Equal("octocat", l.GetProperty("username").GetString());
        Assert.Equal("s3cret-pw", l.GetProperty("password").GetString());
        Assert.Equal("JBSWY3DPEHPK3PXP", l.GetProperty("totp").GetString());

        var card = items.Single(i => i.GetProperty("type").GetInt32() == 3);
        var c = card.GetProperty("card");
        Assert.Equal("ZHANG SAN", c.GetProperty("cardholderName").GetString());
        Assert.Equal("4111111111111111", c.GetProperty("number").GetString());
        Assert.Equal("Visa", c.GetProperty("brand").GetString());
        Assert.Equal("07", c.GetProperty("expMonth").GetString());
        Assert.Equal("2030", c.GetProperty("expYear").GetString());
        Assert.Equal("123", c.GetProperty("code").GetString());

        var identity = items.Single(i => i.GetProperty("type").GetInt32() == 4);
        var id = identity.GetProperty("identity");
        Assert.Equal("San", id.GetProperty("firstName").GetString());
        Assert.Equal("Zhang", id.GetProperty("lastName").GetString());
        Assert.Equal("san@example.com", id.GetProperty("email").GetString());
        Assert.Equal("13800000000", id.GetProperty("phone").GetString());
        Assert.Equal("110101199001011234", id.GetProperty("ssn").GetString());
        // 本地只有一个 Address，整段进 address1（不猜拆分）
        Assert.Equal("北京市朝阳区某路 1 号", id.GetProperty("address1").GetString());
    }

    /// <summary>
    /// 坑 3：安全笔记正文在 notes 里，但**必须**另外带上 secureNote.type，
    /// 否则 Bitwarden 的 toView 不会给它建 secureNote，导入后类型就废了。
    /// </summary>
    [Fact]
    public void Secure_Note_Keeps_Body_In_Notes_And_Carries_The_Type_Marker()
    {
        SeedAllFourTypes();
        using var doc = ExportJson(out _);

        var note = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("type").GetInt32() == 2);

        Assert.Equal("密码是 hunter2", note.GetProperty("notes").GetString());
        Assert.True(note.TryGetProperty("secureNote", out var sn));
        Assert.Equal(0, sn.GetProperty("type").GetInt32());
    }

    // ═══════════════════════ 字段类型换算（坑 2）═══════════════════════

    [Fact]
    public void Custom_Fields_Use_Type_And_Never_Emit_A_Hidden_Key()
    {
        SeedAllFourTypes();
        using var doc = ExportJson(out _);

        var login = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("type").GetInt32() == 1);
        var fields = login.GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal(3, fields.Count);

        // 0=文本 1=隐藏 2=布尔（FieldType）
        Assert.Equal(1, fields[0].GetProperty("type").GetInt32());   // Type=1, Hidden=true
        Assert.Equal("PIN", fields[0].GetProperty("name").GetString());
        Assert.Equal(0, fields[1].GetProperty("type").GetInt32());   // Type=0, Hidden=false
        Assert.Equal(1, fields[2].GetProperty("type").GetInt32());   // Type=0 但 Hidden=true → 兜底成隐藏

        foreach (var f in fields)
        {
            // 导出格式里没有 hidden 这个字段；带上它 Bitwarden 会直接忽略，
            // 但那样掩盖的正是"隐藏字段被当明文"的 bug
            Assert.False(f.TryGetProperty("hidden", out _));
        }
    }

    // ═══════════════════════ 文件夹绑定 ═══════════════════════

    [Fact]
    public void Item_FolderId_References_An_Folder_Id_That_Is_Actually_Exported()
    {
        SeedAllFourTypes();
        using var doc = ExportJson(out _);
        var root = doc.RootElement;

        var exportedIds = root.GetProperty("folders").EnumerateArray()
            .Select(f => f.GetProperty("id").GetString()).ToHashSet();

        var login = root.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("type").GetInt32() == 1);

        // 导入器靠 groupingsMap.has(c.folderId) 建立归属；对不上就是静默落"未分类"
        var folderId = login.GetProperty("folderId").GetString();
        Assert.NotNull(folderId);
        Assert.Contains(folderId, exportedIds);
        Assert.Equal(_folderId.ToString(), folderId);
    }

    /// <summary>指向一个不导出的文件夹（悬空 Id）时宁可不写，别留一个匹配不上的引用。</summary>
    [Fact]
    public void Dangling_FolderId_Is_Dropped_Instead_Of_Being_Written()
    {
        _vault.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(), Type = CipherType.Login, Name = "悬空",
            FolderId = Guid.NewGuid(),          // 库里没有这个文件夹
            SyncStatus = "synced",
        });
        _vault.SaveChanges();

        using var doc = ExportJson(out _);
        var item = doc.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.False(item.TryGetProperty("folderId", out var fid) && fid.ValueKind != JsonValueKind.Null);
    }

    // ═══════════════════════ 登录网址形状 ═══════════════════════

    [Fact]
    public void Login_Uris_Are_Bitwarden_Uri_Objects_Not_Bare_Strings()
    {
        SeedAllFourTypes();
        using var doc = ExportJson(out _);

        var login = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("type").GetInt32() == 1);
        var uris = login.GetProperty("login").GetProperty("uris").EnumerateArray().ToList();

        Assert.Equal(2, uris.Count);
        // 必须是 [{uri, match}]，写成 ["https://..."] 导入器会拿到 undefined
        Assert.Equal("https://github.com", uris[0].GetProperty("uri").GetString());
        Assert.Equal("https://gist.github.com", uris[1].GetProperty("uri").GetString());
    }

    /// <summary>没有网址的登录也要给空的 uris 数组（Bitwarden 自己的导出就是这么给的）。</summary>
    [Fact]
    public void Login_Without_Uris_Still_Emits_An_Empty_Uris_Array()
    {
        _vault.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(), Type = CipherType.Login, Name = "无网址", SyncStatus = "synced",
            Login = new CipherLogin { Username = "u", Password = "p" },
        });
        _vault.SaveChanges();

        using var doc = ExportJson(out _);
        var uris = doc.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("login").GetProperty("uris");

        Assert.Equal(JsonValueKind.Array, uris.ValueKind);
        Assert.Equal(0, uris.GetArrayLength());
    }

    /// <summary>悬空/已删条目不该混进给 Bitwarden 的文件里。</summary>
    [Fact]
    public void Soft_Deleted_Ciphers_Are_Not_Exported()
    {
        SeedAllFourTypes();
        var gone = _vault.Ciphers.First(c => c.Name == "Visa");
        gone.DeletedAt = DateTime.UtcNow;
        _vault.SaveChanges();

        using var doc = ExportJson(out var file);
        Assert.Equal(3, file.Items.Count);
        Assert.DoesNotContain(file.Items, i => i.Name == "Visa");
    }

    /// <summary>
    /// 时间戳必须带 <c>Z</c>。
    ///
    /// 这条是**看着真实输出**才发现的问题：EF 的 SQLite provider 把 DateTime 存成 TEXT、
    /// 读回来是 Kind=Unspecified，System.Text.Json 于是输出 <c>"2026-01-01T00:00:00"</c>。
    /// Bitwarden 用 <c>new Date(...)</c> 解析不带时区的 ISO 串会当成**本地时间**——
    /// 东八区下导入后的创建/修改时间整整偏 8 小时。
    /// </summary>
    [Fact]
    public void Timestamps_Carry_An_Explicit_Utc_Marker()
    {
        _vault.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(), Type = CipherType.Login, Name = "带时间", SyncStatus = "synced",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
        });
        _vault.SaveChanges();

        using var doc = ExportJson(out _);
        var item = doc.RootElement.GetProperty("items").EnumerateArray().Single();

        var created = item.GetProperty("creationDate").GetString()!;
        var revised = item.GetProperty("revisionDate").GetString()!;

        Assert.StartsWith("2026-01-01T00:00:00", created);
        Assert.EndsWith("Z", created);
        Assert.StartsWith("2026-02-02T00:00:00", revised);
        Assert.EndsWith("Z", revised);
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
