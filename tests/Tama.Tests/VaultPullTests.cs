using Tama.Core.Contracts;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Auth;
using Tama.Services.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 拉取（AuthRefresh）的两个新约定：
///   1. **一行都没变的条目不重写**。RevisionDate 是 Bitwarden 的内容版本号，而 ApplyRemote 每次都
///      把它落在本地 UpdatedAt 上 → 相等即"同一个版本"，重写一遍只是把同样的值再写一次
///      （实测全表 198 行要 162–507ms）。副作用是好的：Updated 从"每次都是 198"变成真实的变化计数，
///      定时轮询据此决定要不要提示用户。
///   2. **全量拉取有进程级闸门**：解锁同步 / 手动同步 / 定时轮询可能落在不同 scope，
///      同时跑两趟会互相覆盖、界面还会各弹一次提示。
/// </summary>
public class VaultPullTests : IDisposable
{
    private const string AccountId = "acct-1";

    private readonly SqliteConnection _vaultConn, _authConn;
    private readonly TamaDbContext _vault;
    private readonly AuthDbContext _auth;
    private readonly FakeBitwardenClient _client = new();
    private readonly VaultSyncGate _gate = new();
    private readonly AuthService _authService;

    private static readonly DateTime Rev1 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Rev2 = new(2026, 9, 2, 8, 30, 0, DateTimeKind.Utc);

    public VaultPullTests()
    {
        _vaultConn = new SqliteConnection("DataSource=:memory:");
        _vaultConn.Open();
        _authConn = new SqliteConnection("DataSource=:memory:");
        _authConn.Open();

        _vault = new TamaDbContext(new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(_vaultConn).Options);
        _auth = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_authConn).Options);
        _vault.Database.EnsureCreated();
        _auth.Database.EnsureCreated();

        var accounts = new BitwardenAccountService(_auth, null!, _client);
        _authService = new AuthService(null!, _client, _auth, _vault, null!, accounts, null!,
            new VaultSyncNotifier(), _gate);
    }

    private RefreshRequest Request() => new()
    {
        AccountId = AccountId,
        AccessToken = "token",
        DerivedEncKey = Convert.ToBase64String(new byte[32]),
        DerivedMacKey = Convert.ToBase64String(new byte[32]),
    };

    private Cipher SeedSynced(string name, DateTime revision)
    {
        var c = new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Login,
            Name = name,
            SyncStatus = "synced",
            CreatedAt = Rev1,
            UpdatedAt = revision,
            Login = new CipherLogin { Username = "u", Password = "p" },
        };
        _vault.Ciphers.Add(c);
        _vault.SaveChanges();
        return c;
    }

    private void ServerReturns(params BitwardenCipherResponse[] ciphers)
    {
        _client.SyncResponse = new BitwardenSyncResponse
        {
            IsFullSync = true,
            Ciphers = ciphers.ToList(),
            Folders = new(),
            Profile = new BitwardenProfileResponse { Id = "u1", Email = "u@example.com" },
            MaxRevisionDate = DateTime.UtcNow,
        };
    }

    private static BitwardenCipherResponse ServerCipher(Guid id, string name, DateTime revision) => new()
    {
        Id = id.ToString(),
        Type = 1,
        Name = name,
        RevisionDate = revision,
        Login = new BitwardenLoginData { Username = "u", Password = "p" },
    };

    [Fact]
    public async Task Unchanged_Row_Is_Not_Rewritten()
    {
        var local = SeedSynced("GitHub", Rev1);
        ServerReturns(ServerCipher(local.Id, "GitHub", Rev1));

        var r = await _authService.AuthRefresh(Request());

        Assert.Equal(0, r.Imported);
        Assert.Equal(0, r.Updated);      // ← 同一个 RevisionDate：没有必要重写
        Assert.Equal(0, r.Removed);
        Assert.Equal(1, _client.SyncCalls);
    }

    [Fact]
    public async Task Changed_Row_Is_Still_Applied()
    {
        var local = SeedSynced("GitHub", Rev1);
        ServerReturns(ServerCipher(local.Id, "GitHub（服务器上改过）", Rev2));

        var r = await _authService.AuthRefresh(Request());

        Assert.Equal(1, r.Updated);
        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == local.Id);
        Assert.Equal("GitHub（服务器上改过）", after.Name);
        Assert.Equal(Rev2, after.UpdatedAt);   // 记住这一版，下一轮就能跳过它
    }

    [Fact]
    public async Task New_Server_Row_Is_Imported()    {
        var serverId = Guid.NewGuid();
        ServerReturns(ServerCipher(serverId, "手机上新建的", Rev2));

        var r = await _authService.AuthRefresh(Request());

        Assert.Equal(1, r.Imported);
        Assert.Equal(0, r.Updated);
        Assert.True(await _vault.Ciphers.AnyAsync(c => c.Id == serverId));
    }

    /// <summary>
    /// **RevisionDate 相等 ≠ 本地不缺东西**（2026-09-13 实测的真 bug）。
    /// 用户那 198 行是 <c>CipherMapper</c> 出现之前的旧内联映射写的（只填 Login/Notes），
    /// 而"跳过重写"与 CipherMapper 是同一个提交进来的 → 从那以后每次同步都报 "198 unchanged"，
    /// 卡片 / 自定义字段 / 通行密钥在本地**永远补不回来**。
    /// 表面症状：主页面「通行密钥」筛选恒为 0、点不动（那个分面数的是本地行）。
    /// </summary>
    [Fact]
    public async Task Local_Row_Missing_Server_Data_Is_Rewritten_Even_When_Revision_Matches()
    {
        var local = SeedSynced("Google", Rev1);   // 旧代码写的：没有通行密钥、没有自定义字段
        var server = ServerCipher(local.Id, "Google", Rev1);
        server.Fido2Credentials = new()
        {
            new BitwardenFido2Credential { CredentialId = "cred-1", RpId = "google.com", KeyValue = "k" },
        };
        server.Fields = new() { new BitwardenFieldData { Name = "pin", Value = "1234" } };
        ServerReturns(server);

        var r = await _authService.AuthRefresh(Request());

        Assert.Equal(1, r.Updated);               // ← 缺东西就重写，哪怕 RevisionDate 没变
        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == local.Id);
        Assert.Single(after.Fido2Credentials!);
        Assert.Single(after.Fields!);
    }

    /// <summary>
    /// 反例：本地那份什么都不缺 → 照旧跳过。没有这一条，"每轮都重写"会以"功能正常"的样子溜进来
    /// （Updated 永远非 0 = 定时拉每 10 分钟弹一次"已从云端同步"）。
    /// 判据特意取"自有集合条数 + 可空依赖有没有非空字段"，就是为了不被 EF 的
    /// optional-dependent 不 materialize 坑到（那样会每轮都判成"缺"）。
    /// </summary>
    [Fact]
    public async Task Faithful_Local_Row_Is_Still_Skipped()
    {
        var local = new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Card,
            Name = "Visa",
            SyncStatus = "synced",
            UpdatedAt = Rev1,
            Card = new CipherCard { Number = "4111111111111111" },
            Fields = new() { new CipherField { Id = 0, Name = "pin", Value = "1234" } },
            Fido2Credentials = new()
            {
                new Fido2Credential { Id = 0, CredentialId = "cred-1", RpId = "x.example", KeyValue = "k" },
            },
        };
        _vault.Ciphers.Add(local);
        await _vault.SaveChangesAsync();

        ServerReturns(new BitwardenCipherResponse
        {
            Id = local.Id.ToString(),
            Type = (int)CipherType.Card,
            Name = "Visa",
            RevisionDate = Rev1,
            Card = new BitwardenCardData { Number = "4111111111111111" },
            Fields = new() { new BitwardenFieldData { Name = "pin", Value = "1234" } },
            Fido2Credentials = new()
            {
                new BitwardenFido2Credential { CredentialId = "cred-1", RpId = "x.example", KeyValue = "k" },
            },
        });

        var r = await _authService.AuthRefresh(Request());

        Assert.Equal(0, r.Updated);
        Assert.Equal(0, r.Imported);
    }

    [Fact]
    public async Task Concurrent_Pull_Is_Skipped_Without_Touching_The_Network()
    {
        ServerReturns();

        // 假装另一个同步（解锁 / 手动 / 定时）正在跑
        Assert.True(_gate.TryEnter());

        var skipped = await _authService.AuthRefresh(Request());

        Assert.Equal(0, skipped.Total);
        Assert.Equal(0, _client.SyncCalls);   // 关键：根本没发请求

        _gate.Exit();

        var ran = await _authService.AuthRefresh(Request());
        Assert.Equal(0, ran.Total);
        Assert.Equal(1, _client.SyncCalls);
    }

    public void Dispose()
    {
        _vault.Dispose();
        _auth.Dispose();
        _vaultConn.Dispose();
        _authConn.Dispose();
    }
}
