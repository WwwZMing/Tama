using System.Text;
using System.Text.Json;
using Tama.Core.Contracts;
using Tama.Core.Formats;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Auth;
using Tama.Services.Import;
using Tama.Services.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 「导出 → 导入」往返：备份的全部内容必须原样回到库里。
///
/// 为什么值得单独一组测试：v1 的导出只写 Login，银行卡 / 身份信息 / 标签 / 自定义字段
/// **静默丢失**——而这种丢失在界面上完全看不出来（条目数对得上，点进去才发现卡片是空的）。
/// 用户拿这份备份换机器时才会发现，那时候原库已经没了。
///
/// 数据刻意"凑全"：四种类型 + 标签 + 自定义字段 + 收藏 + 文件夹 + 自定义时间戳，
/// 每一样都是 v1 会丢的东西。
/// </summary>
public class VaultRoundTripTests : IDisposable
{
    private static readonly byte[] DbKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly List<IDisposable> _disposables = new();
    private readonly FakeBitwardenClient _client = new();

    // ───────────────────────── 环境搭建 ─────────────────────────

    private (SqliteConnection conn, TamaDbContext db) NewVault()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new TamaDbContext(new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        _disposables.Add(conn);
        _disposables.Add(db);
        return (conn, db);
    }

    private (SqliteConnection conn, AuthDbContext db) NewAuth()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        _disposables.Add(conn);
        _disposables.Add(db);
        return (conn, db);
    }

    private BitwardenAccountService Accounts(AuthDbContext auth)
    {
        var keys = new DatabaseKeyService();
        keys.SetKey(DbKey);
        return new BitwardenAccountService(auth, keys, _client);
    }

    private CipherService Cipher(TamaDbContext vault, AuthDbContext auth)
        => new(vault, Accounts(auth), _client);

    /// <summary>纯本地库（无 Bitwarden 账号）：导入不碰网络，断言只看本地落库结果。</summary>
    private VaultImportService Importer(TamaDbContext vault, AuthDbContext auth)
        => new(Cipher(vault, auth), new FolderService(auth, vault, Accounts(auth), _client), vault);

    // ───────────────────────── 造数据 ─────────────────────────

    private static readonly Guid WorkFolderId = Guid.NewGuid();

    private static void SeedSourceVault(TamaDbContext db)
    {
        db.Folders.Add(new Folder { Id = WorkFolderId, Name = "工作" });

        db.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Login,
            Name = "GitHub",
            Notes = "恢复码在保险箱",
            Favorite = true,
            FolderId = WorkFolderId,
            SyncStatus = "synced",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            Tags = new() { "dev", "重要" },
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
        });

        db.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Card,
            Name = "Visa",
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
        });

        db.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.SecureNote,
            Name = "WiFi 密码",
            Notes = "密码是 hunter2",
            SyncStatus = "synced",
            SecureNote = new CipherNote(),
        });

        db.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Identity,
            Name = "我的身份",
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
        });

        db.SaveChanges();
    }

    /// <summary>走真实的序列化设置把导出内容变成字节（模拟用户拿到的那个 .json 文件）。</summary>
    private static byte[] Serialize(TamaVaultFile file)
        => JsonSerializer.SerializeToUtf8Bytes(file, TamaVaultJson.WriteOptions);

    // ═══════════════════════ 主用例：完整往返 ═══════════════════════

    [Fact]
    public async Task Export_Then_Import_Restores_All_Four_Types_Faithfully()
    {
        // --- 源库 + 导出 ---
        var (_, srcVault) = NewVault();
        var (_, srcAuth) = NewAuth();
        SeedSourceVault(srcVault);

        var file = Cipher(srcVault, srcAuth).BuildExportFile();
        Assert.Equal(4, file.Count);
        Assert.Equal(TamaVaultJson.CurrentVersion, file.Version);

        var bytes = Serialize(file);

        // --- 目标库：全新空库，导入 ---
        var (_, dstVault) = NewVault();
        var (_, dstAuth) = NewAuth();
        var importer = Importer(dstVault, dstAuth);

        var preview = importer.Preview(bytes);
        Assert.True(preview.IsTamaVault, preview.Error);
        Assert.Equal(4, preview.Total);
        Assert.Equal(2, preview.Version);
        Assert.Single(preview.Folders);
        Assert.Equal("工作", preview.Folders[0]);
        // 预览要按类型分开报数，四种各 1
        Assert.Equal(4, preview.Types.Count);
        Assert.All(preview.Types, t => Assert.Equal(1, t.Count));

        var result = await importer.Import(bytes);
        Assert.Equal(4, result.Imported);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.FoldersCreated);      // 「工作」被建出来
        Assert.Empty(result.Warnings);

        var restored = await dstVault.Ciphers.AsNoTracking().ToListAsync();
        Assert.Equal(4, restored.Count);

        // === 登录：TOTP / 多网址 / 备注 / 收藏 / 标签 / 自定义字段 / 文件夹 / 时间戳 一个都不能少 ===
        var login = restored.Single(c => c.Type == CipherType.Login);
        Assert.Equal("GitHub", login.Name);
        Assert.Equal("恢复码在保险箱", login.Notes);
        Assert.True(login.Favorite);
        Assert.Equal("octocat", login.Login!.Username);
        Assert.Equal("s3cret-pw", login.Login.Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", login.Login.Totp);
        Assert.Equal(new[] { "https://github.com", "https://gist.github.com" }, login.Login.Uris.ToArray());
        Assert.Equal(new[] { "dev", "重要" }, login.Tags.ToArray());
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), login.CreatedAt);
        Assert.Equal(new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc), login.UpdatedAt);

        Assert.NotNull(login.Fields);
        Assert.Equal(2, login.Fields!.Count);
        // 自有集合的读取顺序 EF 不保证，按 Id 排过再断言
        var fields = login.Fields.OrderBy(f => f.Id).ToList();
        Assert.Equal("PIN", fields[0].Name);
        Assert.Equal("1234", fields[0].Value);
        Assert.True(fields[0].Hidden);
        Assert.Equal(1, fields[0].Type);
        Assert.Equal(1, fields[1].Id);               // 复合主键后半截必须重新编号成 0,1

        // 文件夹按名字还原，且条目确实挂上去了
        var folder = await dstVault.Folders.AsNoTracking().SingleAsync();
        Assert.Equal("工作", folder.Name);
        Assert.Equal(folder.Id, login.FolderId);

        // === 银行卡：v1 会整段丢掉 ===
        var card = restored.Single(c => c.Type == CipherType.Card);
        Assert.Equal("Visa", card.Name);
        Assert.NotNull(card.Card);
        Assert.Equal("ZHANG SAN", card.Card!.CardholderName);
        Assert.Equal("4111111111111111", card.Card.Number);
        Assert.Equal("Visa", card.Card.Brand);
        Assert.Equal("07", card.Card.ExpMonth);
        Assert.Equal("2030", card.Card.ExpYear);
        Assert.Equal("123", card.Card.Code);

        // === 安全笔记：正文在 Notes 里 ===
        var note = restored.Single(c => c.Type == CipherType.SecureNote);
        Assert.Equal("WiFi 密码", note.Name);
        Assert.Equal("密码是 hunter2", note.Notes);

        // === 身份信息：v1 会整段丢掉 ===
        var identity = restored.Single(c => c.Type == CipherType.Identity);
        Assert.Equal("我的身份", identity.Name);
        Assert.NotNull(identity.Identity);
        Assert.Equal("San", identity.Identity!.FirstName);
        Assert.Equal("Zhang", identity.Identity.LastName);
        Assert.Equal("san@example.com", identity.Identity.Email);
        Assert.Equal("13800000000", identity.Identity.Phone);
        Assert.Equal("110101199001011234", identity.Identity.Ssn);
        Assert.Equal("san", identity.Identity.Username);
        Assert.Equal("北京市朝阳区某路 1 号", identity.Identity.Address);
    }

    // ═══════════════════════ 重复导入 ═══════════════════════

    [Fact]
    public async Task Import_Twice_Skips_Duplicates_Instead_Of_Doubling_The_Vault()
    {
        var (_, srcVault) = NewVault();
        var (_, srcAuth) = NewAuth();
        SeedSourceVault(srcVault);
        var bytes = Serialize(Cipher(srcVault, srcAuth).BuildExportFile());

        var (_, dstVault) = NewVault();
        var (_, dstAuth) = NewAuth();
        var importer = Importer(dstVault, dstAuth);

        var first = await importer.Import(bytes);
        Assert.Equal(4, first.Imported);

        // 第二次：同一份文件，默认应该一条都不再进
        var second = await importer.Import(bytes);
        Assert.Equal(0, second.Imported);
        Assert.Equal(4, second.Skipped);
        Assert.Equal(0, second.FoldersCreated);      // 同名文件夹复用，不重复建

        Assert.Equal(4, await dstVault.Ciphers.CountAsync());
        Assert.Equal(1, await dstVault.Folders.CountAsync());
    }

    [Fact]
    public async Task Import_With_SkipDuplicates_Off_Creates_A_Second_Copy()
    {
        var (_, srcVault) = NewVault();
        var (_, srcAuth) = NewAuth();
        SeedSourceVault(srcVault);
        var bytes = Serialize(Cipher(srcVault, srcAuth).BuildExportFile());

        var (_, dstVault) = NewVault();
        var (_, dstAuth) = NewAuth();
        var importer = Importer(dstVault, dstAuth);

        await importer.Import(bytes);
        var again = await importer.Import(bytes, new VaultImportOptions { SkipDuplicates = false });

        Assert.Equal(4, again.Imported);
        Assert.Equal(8, await dstVault.Ciphers.CountAsync());
    }

    [Fact]
    public async Task Import_Without_RestoreFolders_Puts_Everything_Unfiled()
    {
        var (_, srcVault) = NewVault();
        var (_, srcAuth) = NewAuth();
        SeedSourceVault(srcVault);
        var bytes = Serialize(Cipher(srcVault, srcAuth).BuildExportFile());

        var (_, dstVault) = NewVault();
        var (_, dstAuth) = NewAuth();
        var importer = Importer(dstVault, dstAuth);

        var result = await importer.Import(bytes, new VaultImportOptions { RestoreFolders = false });

        Assert.Equal(4, result.Imported);
        Assert.Equal(0, result.FoldersCreated);
        Assert.Equal(0, await dstVault.Folders.CountAsync());
        Assert.All(await dstVault.Ciphers.AsNoTracking().ToListAsync(), c => Assert.Null(c.FolderId));
    }

    // ═══════════════════════ 老文件与坏文件 ═══════════════════════

    /// <summary>
    /// v1（扁平 username/password/totp/uris）必须还能读——用户手上可能已经有 v1 导出的文件了。
    /// </summary>
    [Fact]
    public async Task Import_Accepts_Legacy_V1_Flat_Format()
    {
        const string v1 = """
        {
          "format": "tama-json",
          "version": 1,
          "exportedAt": "2026-01-01T00:00:00Z",
          "count": 1,
          "items": [
            {
              "name": "老条目",
              "type": "Login",
              "folder": "旧文件夹",
              "favorite": true,
              "username": "old-user",
              "password": "old-pw",
              "totp": "JBSWY3DPEHPK3PXP",
              "uris": ["https://old.example"],
              "notes": "旧备注",
              "createdAt": "2026-01-01T00:00:00Z",
              "updatedAt": "2026-01-02T00:00:00Z"
            }
          ]
        }
        """;

        var (_, dstVault) = NewVault();
        var (_, dstAuth) = NewAuth();
        var importer = Importer(dstVault, dstAuth);

        var bytes = Encoding.UTF8.GetBytes(v1);
        var preview = importer.Preview(bytes);
        Assert.True(preview.IsTamaVault, preview.Error);
        Assert.Equal(1, preview.Version);

        var result = await importer.Import(bytes);
        Assert.Equal(1, result.Imported);

        var c = await dstVault.Ciphers.AsNoTracking().SingleAsync();
        Assert.Equal(CipherType.Login, c.Type);
        Assert.Equal("老条目", c.Name);
        Assert.Equal("old-user", c.Login!.Username);
        Assert.Equal("old-pw", c.Login.Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", c.Login.Totp);
        Assert.Equal("https://old.example", Assert.Single(c.Login.Uris));
        Assert.Equal("旧备注", c.Notes);
        Assert.True(c.Favorite);
    }

    /// <summary>
    /// 别家的导出（Bitwarden / 1Password）也是**合法 JSON**。不靠 format 标记挡住的话，
    /// 它们会被当成"0 条"静默导入成功——用户以为恢复了，其实一个字都没进来。
    /// </summary>
    [Fact]
    public void Preview_Rejects_Foreign_Json()
    {
        var (_, dstVault) = NewVault();
        var (_, dstAuth) = NewAuth();
        var importer = Importer(dstVault, dstAuth);

        // type 刻意写成字符串，否则会因为"数字塞不进 string 属性"抛 JSON 异常而过关——
        // 那样测到的是反序列化失败，不是 format 校验本身。
        var bitwardenish = Encoding.UTF8.GetBytes("""
        { "encrypted": false, "folders": [], "items": [ { "name": "GitHub", "type": "Login" } ] }
        """);

        var preview = importer.Preview(bitwardenish);
        Assert.False(preview.IsTamaVault);
        Assert.NotNull(preview.Error);
        Assert.Equal(0, preview.Total);
    }

    [Fact]
    public void Preview_Rejects_Garbage_Without_Throwing()
    {
        var (_, dstVault) = NewVault();
        var (_, dstAuth) = NewAuth();
        var importer = Importer(dstVault, dstAuth);

        var preview = importer.Preview(Encoding.UTF8.GetBytes("这不是 JSON"));
        Assert.False(preview.IsTamaVault);
        Assert.Contains("JSON", preview.Error!);

        Assert.False(importer.Preview(Array.Empty<byte>()).IsTamaVault);
    }

    [Fact]
    public void Preview_Rejects_Newer_Format_Version()
    {
        var (_, dstVault) = NewVault();
        var (_, dstAuth) = NewAuth();
        var importer = Importer(dstVault, dstAuth);

        var future = Encoding.UTF8.GetBytes("""{ "format": "tama-json", "version": 99, "items": [] }""");

        var preview = importer.Preview(future);
        Assert.False(preview.IsTamaVault);
        Assert.Contains("v99", preview.Error!);
    }

    [Fact]
    public async Task Import_Of_Foreign_Json_Throws_Instead_Of_Silently_Doing_Nothing()
    {
        var (_, dstVault) = NewVault();
        var (_, dstAuth) = NewAuth();
        var importer = Importer(dstVault, dstAuth);

        await Assert.ThrowsAsync<Tama.Core.Exceptions.TamaException>(
            () => importer.Import(Encoding.UTF8.GetBytes("""{ "items": [] }""")));
    }

    /// <summary>没有 type 字段时按带回来的段落反推——一张银行卡不能因为缺类型被塞成登录条目。</summary>
    [Fact]
    public async Task Import_Infers_Type_From_Payload_When_Type_Is_Missing()
    {
        var json = """
        {
          "format": "tama-json", "version": 2, "count": 1,
          "items": [ { "name": "无类型卡", "card": { "number": "4111111111111111", "brand": "Visa" } } ]
        }
        """;

        var (_, dstVault) = NewVault();
        var (_, dstAuth) = NewAuth();
        var importer = Importer(dstVault, dstAuth);

        await importer.Import(Encoding.UTF8.GetBytes(json));

        var c = await dstVault.Ciphers.AsNoTracking().SingleAsync();
        Assert.Equal(CipherType.Card, c.Type);
        Assert.Equal("4111111111111111", c.Card!.Number);
    }

    // ═══════════════════════ 真落盘那一半 ═══════════════════════

    /// <summary>
    /// 上面几条都只测了 <c>BuildExportFile()</c>（纯构建）。落盘那一半也要盖：
    /// 序列化对了但目录/文件名写歪了，用户拿到的是"点了导出、找不到文件"。
    ///
    /// 临时把 <c>HOME</c> 指到临时目录，**不往用户真实的 ~/Downloads 里写**；
    /// 万一重定向没生效，finally 也会按返回的路径把文件删掉。
    /// </summary>
    [Fact]
    public async Task Export_Writes_A_File_That_Can_Be_Imported_Back()
    {
        var (_, srcVault) = NewVault();
        var (_, srcAuth) = NewAuth();
        SeedSourceVault(srcVault);

        var fakeHome = Path.Combine(Path.GetTempPath(), "tama-test-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeHome);
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", fakeHome);

        VaultExportResponse? response = null;
        try
        {
            response = await Cipher(srcVault, srcAuth).Export();

            Assert.Equal(4, response.Count);
            Assert.True(File.Exists(response.Path), $"导出文件不存在：{response.Path}");
            Assert.StartsWith(fakeHome, response.Path);      // 确认真的被重定向了，没碰用户目录
            Assert.EndsWith(".json", response.Path);

            // 落盘的那份字节 → 导回一个全新库
            var bytes = await File.ReadAllBytesAsync(response.Path);

            var (_, dstVault) = NewVault();
            var (_, dstAuth) = NewAuth();
            var result = await Importer(dstVault, dstAuth).Import(bytes);

            Assert.Equal(4, result.Imported);
            Assert.Equal(4, await dstVault.Ciphers.CountAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            try { if (response != null && File.Exists(response.Path)) File.Delete(response.Path); } catch { /* 清理失败不影响断言 */ }
            try { Directory.Delete(fakeHome, recursive: true); } catch { /* 同上 */ }
        }
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        GC.SuppressFinalize(this);
    }
}