using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tama.Core.Contracts;
using Tama.Core.Formats;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Auth;
using Tama.Services.Biometric;
using Tama.Services.Import;
using Tama.Services.Passkey;
using Tama.Services.Vault;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 通行密钥必须跟着**同一份备份**走（tama-json v3）。
///
/// 历史：导出曾经"刻意不导出通行密钥私钥"，理由是"别让明文备份再多一份私钥"。
/// 但那份文件本来就写着全部明文密码，所以这个取舍没换来安全性，只换来一个后果——
/// 通行密钥**只有导入入口、没有导出入口**，换机器 / 重装就永久丢掉（用户 2026-11-14 报的）。
/// 这组测试盯三件事：能导出、能往返、老文件（v2，没有 passkeys）照读不误。
/// </summary>
public class PasskeyBackupTests : IDisposable
{
    private static readonly byte[] DbKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime CreatedAt = new(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    private readonly List<IDisposable> _disposables = new();
    private readonly FakeBitwardenClient _client = new();
    private readonly DatabaseKeyService _keys = new();

    public PasskeyBackupTests() => _keys.SetKey(DbKey);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    // ───────────────────────── 环境 ─────────────────────────

    private TamaDbContext NewVault()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new TamaDbContext(new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        _disposables.Add(conn);
        _disposables.Add(db);
        return db;
    }

    private AuthDbContext NewAuth()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        _disposables.Add(conn);
        _disposables.Add(db);
        return db;
    }

    private PasskeyService Passkeys(TamaDbContext vault, AuthDbContext auth)
        => new(auth, vault, _keys, new NullPasskeyPlatformService());

    private CipherService Ciphers(TamaDbContext vault, AuthDbContext auth)
        => new(vault, new BitwardenAccountService(auth, _keys, _client), _client, Passkeys(vault, auth));

    private VaultImportService Importer(TamaDbContext vault, AuthDbContext auth)
        => new(Ciphers(vault, auth),
            new FolderService(auth, vault, new BitwardenAccountService(auth, _keys, _client), _client),
            vault,
            Passkeys(vault, auth));

    // ───────────────────────── 造一枚本机凭据 ─────────────────────────

    /// <summary>造一枚真实可用的软凭据（EC P-256），返回它的 PKCS#8 私钥以便断言往返后没变。</summary>
    private static byte[] SeedPasskey(TamaDbContext db)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
        var spki = ecdsa.ExportSubjectPublicKeyInfo();

        db.PasskeyCredentials.Add(new PasskeyCredentialRecord
        {
            CredentialId = PasskeyService.Base64Url(SHA256.HashData(spki)),
            RpId = "github.com",
            RpName = "GitHub",
            UserName = "erica@example.com",
            UserHandle = PasskeyService.Base64Url(Encoding.UTF8.GetBytes("account-1")),
            PublicKey = Convert.ToBase64String(spki),
            EncryptedPrivateKey = PasskeyService.EncryptKey(pkcs8, DbKey),
            Counter = 3,
            CreatedAt = CreatedAt,
        });
        db.SaveChanges();
        return pkcs8;
    }

    private static byte[] Serialize(TamaVaultFile file) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file, TamaVaultJson.WriteOptions));

    // ───────────────────────── 测试 ─────────────────────────

    [Fact]
    public void Export_Includes_Passkey_Private_Key()
    {
        var vault = NewVault();
        var auth = NewAuth();
        var pkcs8 = SeedPasskey(vault);

        var file = Ciphers(vault, auth).BuildExportFile();

        Assert.Equal(TamaVaultJson.CurrentVersion, file.Version);
        var passkey = Assert.Single(file.Passkeys!);
        Assert.Equal("github.com", passkey.RpId);
        Assert.Equal("GitHub", passkey.RpName);
        Assert.Equal("erica@example.com", passkey.UserName);
        Assert.Equal(3, passkey.Counter);
        Assert.Equal(CreatedAt, passkey.CreatedAt);

        // keyValue 是**私钥本身**（base64url 的 PKCS#8），与 Bitwarden 导出的口径一致
        Assert.Equal(pkcs8, PasskeyService.DecodeBase64Url(passkey.KeyValue!.TrimEnd('=')));
    }

    [Fact]
    public void Export_Omits_Passkeys_Section_When_There_Are_None()
    {
        var vault = NewVault();
        var auth = NewAuth();

        var file = Ciphers(vault, auth).BuildExportFile();

        Assert.Null(file.Passkeys);
    }

    [Fact]
    public async Task RoundTrip_Restores_Passkey()
    {
        var sourceVault = NewVault();
        var sourceAuth = NewAuth();
        var pkcs8 = SeedPasskey(sourceVault);
        sourceVault.Ciphers.Add(new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Login,
            Name = "GitHub",
            SyncStatus = "synced",
            Login = new CipherLogin { Username = "erica@example.com", Password = "pg" },
        });
        await sourceVault.SaveChangesAsync();

        var json = Serialize(Ciphers(sourceVault, sourceAuth).BuildExportFile());

        var targetVault = NewVault();
        var targetAuth = NewAuth();
        var result = await Importer(targetVault, targetAuth).Import(json);

        Assert.Equal(1, result.PasskeysImported);
        Assert.Empty(result.Warnings);

        var restored = await targetVault.PasskeyCredentials.AsNoTracking().SingleAsync();
        Assert.Equal("github.com", restored.RpId);
        Assert.Equal("erica@example.com", restored.UserName);
        Assert.Equal(3, restored.Counter);
        // 私钥原样回来（这枚凭据还原后还能用来登录，不是只有元数据）
        Assert.Equal(pkcs8, PasskeyService.DecryptKey(restored.EncryptedPrivateKey, DbKey));
    }

    [Fact]
    public async Task Import_Is_Idempotent_For_Passkeys()
    {
        var sourceVault = NewVault();
        var sourceAuth = NewAuth();
        SeedPasskey(sourceVault);
        var json = Serialize(Ciphers(sourceVault, sourceAuth).BuildExportFile());

        var vault = NewVault();
        var auth = NewAuth();
        var importer = Importer(vault, auth);

        var first = await importer.Import(json);
        var second = await importer.Import(json);

        Assert.Equal(1, first.PasskeysImported);
        Assert.Equal(0, second.PasskeysImported);
        Assert.Equal(1, await vault.PasskeyCredentials.CountAsync());   // 不重复、不覆盖
    }

    [Fact]
    public async Task V2_Backup_Without_Passkeys_Still_Imports()
    {
        var vault = NewVault();
        var auth = NewAuth();

        // v2 文件：只有条目，没有 passkeys 字段（现实中用户手里的老备份就是这样）
        var v2 = """
        {
          "format": "tama-json",
          "version": 2,
          "exportedAt": "2026-03-01T00:00:00",
          "count": 1,
          "items": [
            { "name": "Old Login", "type": "Login", "login": { "username": "u", "password": "p" } }
          ]
        }
        """;

        var result = await Importer(vault, auth).Import(Encoding.UTF8.GetBytes(v2));

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.PasskeysImported);
        Assert.Equal(0, await vault.PasskeyCredentials.CountAsync());
    }
}
