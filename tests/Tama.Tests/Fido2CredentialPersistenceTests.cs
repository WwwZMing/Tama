using Tama.Core.Models;
using Tama.Data.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// <c>Cipher.Fido2Credentials</c>（条目附带的通行密钥）的**写入路径**回归。
///
/// 背景：这是 `OwnsMany` 的自有集合，主键是复合的 (CipherId, Id)。EF 默认把键属性当
/// store-generated，于是把 int 列从 INSERT 里省掉，而 SQLite 在复合主键下根本没有生成它的
/// 机制 → `NOT NULL constraint failed: Fido2Credential.Id`。表结构不能改（宿主走
/// EnsureCreated，老库不会迁移），所以改成 `ValueGeneratedNever` + 写入方编号。
///
/// 这几个用例就是那条约定：**写入必须真的能落库、能读回来**，别哪天又被改回默认配置。
/// </summary>
public class Fido2CredentialPersistenceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<TamaDbContext> _options;
    private readonly Guid _cipherId = Guid.NewGuid();

    public Fido2CredentialPersistenceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(_conn).Options;
        using var db = new TamaDbContext(_options);
        db.Database.EnsureCreated();
    }

    private static Cipher LoginWith(params Fido2Credential[] creds) => new()
    {
        Type = CipherType.Login,
        Name = "Bank",
        Login = new CipherLogin { Username = "me" },
        Fido2Credentials = creds.ToList(),
    };

    [Fact]
    public async Task Single_Credential_Roundtrips()
    {
        using (var db = new TamaDbContext(_options))
        {
            var c = LoginWith(new Fido2Credential
            {
                Id = 0,
                CredentialId = "cred-abc",
                RpId = "bank.example",
                RpName = "Bank",
                UserName = "me@example.com",
                UserHandle = "dXNlcg==",
                UserDisplayName = "Me",
                KeyValue = "cHJpdmF0ZS1rZXk=",
                Counter = 7,
                Discoverable = true,
            });
            c.Id = _cipherId;
            db.Ciphers.Add(c);
            await db.SaveChangesAsync();   // ← 修之前这里就是 NOT NULL constraint failed
        }

        using (var db = new TamaDbContext(_options))
        {
            var loaded = await db.Ciphers.AsNoTracking().SingleAsync(x => x.Id == _cipherId);
            var f = Assert.Single(loaded.Fido2Credentials!);
            Assert.Equal(0, f.Id);
            Assert.Equal("cred-abc", f.CredentialId);
            Assert.Equal("bank.example", f.RpId);
            Assert.Equal("Bank", f.RpName);
            Assert.Equal("me@example.com", f.UserName);
            Assert.Equal("dXNlcg==", f.UserHandle);
            Assert.Equal("Me", f.UserDisplayName);
            Assert.Equal("cHJpdmF0ZS1rZXk=", f.KeyValue);
            Assert.Equal(7, f.Counter);
            Assert.True(f.Discoverable);
            // 默认值也别丢：这几列都是 NOT NULL
            Assert.Equal("public-key", f.KeyType);
            Assert.Equal("ECDSA", f.KeyAlgorithm);
            Assert.Equal("P-256", f.KeyCurve);
        }
    }

    /// <summary>一个条目挂多枚凭据：编号 0,1,2… 要能共存（复合主键的后半截就是干这个的）</summary>
    [Fact]
    public async Task Multiple_Credentials_On_One_Cipher_Coexist()
    {
        using (var db = new TamaDbContext(_options))
        {
            var c = LoginWith(
                new Fido2Credential { Id = 0, CredentialId = "c0", RpId = "a.example", KeyValue = "k0" },
                new Fido2Credential { Id = 1, CredentialId = "c1", RpId = "b.example", KeyValue = "k1" },
                new Fido2Credential { Id = 2, CredentialId = "c2", RpId = "c.example", KeyValue = "k2" });
            c.Id = _cipherId;
            db.Ciphers.Add(c);
            await db.SaveChangesAsync();
        }

        using (var db = new TamaDbContext(_options))
        {
            var loaded = await db.Ciphers.AsNoTracking().SingleAsync(x => x.Id == _cipherId);
            Assert.Equal(new[] { 0, 1, 2 }, loaded.Fido2Credentials!.Select(f => f.Id).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "c0", "c1", "c2" }, loaded.Fido2Credentials!.Select(f => f.CredentialId).OrderBy(x => x).ToArray());
        }
    }

    /// <summary>同一条目删掉一枚再存另一枚：不能撞主键，也不能把整条 cipher 一起带走</summary>
    [Fact]
    public async Task Replacing_Credentials_On_Existing_Cipher_Works()
    {
        using (var db = new TamaDbContext(_options))
        {
            var c = LoginWith(new Fido2Credential { Id = 0, CredentialId = "old", RpId = "a.example", KeyValue = "k" });
            c.Id = _cipherId;
            db.Ciphers.Add(c);
            await db.SaveChangesAsync();
        }

        using (var db = new TamaDbContext(_options))
        {
            var c = await db.Ciphers.SingleAsync(x => x.Id == _cipherId);
            c.Fido2Credentials!.Clear();
            c.Fido2Credentials.Add(new Fido2Credential { Id = 0, CredentialId = "new", RpId = "b.example", KeyValue = "k2" });
            await db.SaveChangesAsync();
        }

        using (var db = new TamaDbContext(_options))
        {
            var loaded = await db.Ciphers.AsNoTracking().SingleAsync(x => x.Id == _cipherId);
            var f = Assert.Single(loaded.Fido2Credentials!);
            Assert.Equal("new", f.CredentialId);
            Assert.Equal("b.example", f.RpId);
        }
    }

    /// <summary>
    /// 忘了编号会**响亮地**失败，而不是静默把两枚凭据写成"同一行"。
    /// 实际失败点是 EF 的变更跟踪（不是等到数据库报主键冲突）：两枚凭据算出同一个
    /// (CipherId, Id)，跟踪器直接拒绝——报错里点名了实体和主键列，够人看懂。
    /// 这条是在钉住"Id 由调用方负责"这个约定：连失败方式也必须是可诊断的。
    /// </summary>
    [Fact]
    public async Task Forgetting_To_Number_Credentials_Fails_Loudly()
    {
        using var db = new TamaDbContext(_options);
        var c = LoginWith(
            new Fido2Credential { CredentialId = "dup-a", RpId = "a.example", KeyValue = "k" },
            new Fido2Credential { CredentialId = "dup-b", RpId = "b.example", KeyValue = "k" });
        c.Id = _cipherId;

        Exception? caught = null;
        try
        {
            db.Ciphers.Add(c);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.Contains("Fido2Credential", caught!.Message);
        Assert.Contains("CipherId", caught.Message);
        Assert.Contains("already being tracked", caught.Message);
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}
