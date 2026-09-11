using System.Net;
using Tama.Core.Exceptions;
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
/// "同步失败：All key approaches failed" 的回归（2026-09-13 用户实测）。
///
/// 两个独立的问题叠在一起：
///   ① **access token 过期后没人刷新**。Bitwarden 的 access token 默认 1 小时就失效，
///      而库里存的是登录那一刻那一个；`BitwardenAccountService.TryRefreshToken` 当时**只有测试在调用**
///      → 过期之后每一次同步都 401，唯一出路是重新登录。
///   ② **失败原因被吞掉**。`TrySyncAsync` 把异常降级成一句 Log.Warning（Release 的文件日志只收
///      Error 及以上，连这句都看不到），调用方只能报一句 `All key approaches failed`——
///      401 / 断网 / 响应畸形在界面上长得一模一样。
///
/// 这一组钉住三件事：401 要刷新并**重试一次**、刷新不成功要给出能照做的提示、
/// 断网**不许**被当成过期（否则会白刷一次 token，还会把"网络问题"说成"登录过期"）。
/// </summary>
public class AuthTokenRefreshTests : IDisposable
{
    private static readonly byte[] DbKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private const string AccountId = "acct-1";

    private readonly SqliteConnection _vaultConn, _authConn;
    private readonly TamaDbContext _vault;
    private readonly AuthDbContext _auth;
    private readonly DatabaseKeyService _keyService = new();
    private readonly FakeBitwardenClient _client = new();
    private readonly AuthService _authService;

    public AuthTokenRefreshTests()
    {
        _vaultConn = new SqliteConnection("DataSource=:memory:");
        _vaultConn.Open();
        _authConn = new SqliteConnection("DataSource=:memory:");
        _authConn.Open();

        _vault = new TamaDbContext(new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(_vaultConn).Options);
        _auth = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_authConn).Options);
        _vault.Database.EnsureCreated();
        _auth.Database.EnsureCreated();
        _keyService.SetKey(DbKey);

        var account = new AccountData
        {
            Id = AccountId,
            Email = "me@example.com",
            Type = "bitwarden",
            ServerUrl = "https://vault.bitwarden.com",
            AccessToken = "old-access-token",
            RefreshToken = "old-refresh-token",
            EncryptionKey = "user-key",
            DerivedEncKey = Convert.ToBase64String(new byte[32]),
            DerivedMacKey = Convert.ToBase64String(new byte[32]),
        };
        account.EncryptSensitiveFields(DbKey);   // 真实路径就是这么存的
        _auth.Accounts.Add(account);
        _auth.SaveChanges();

        var accounts = new BitwardenAccountService(_auth, _keyService, _client);
        _authService = new AuthService(null!, _client, _auth, _vault, _keyService, accounts, null!,
            new VaultSyncNotifier(), new VaultSyncGate());

        _client.SyncResponse = new BitwardenSyncResponse
        {
            IsFullSync = true,
            Ciphers = new(),
            Folders = new(),
            Profile = new BitwardenProfileResponse { Id = "u1", Email = "me@example.com" },
            MaxRevisionDate = DateTime.UtcNow,
        };
    }

    private static HttpRequestException Unauthorized() =>
        new("Response status code does not indicate success: 401 (Unauthorized).",
            null, HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Expired_Token_Is_Refreshed_And_Sync_Retries_Once()
    {
        _client.SyncFaults[1] = Unauthorized();      // 第一次：token 过期
        _client.RefreshReturns = new BitwardenRefreshResponse
        {
            AccessToken = "fresh-access-token",
            RefreshToken = "fresh-refresh-token",
        };

        var result = await _authService.SyncNow(AccountId);

        Assert.Equal(2, _client.SyncCalls);
        // 重试必须带着**刷新后**的 token，而不是原来那个
        Assert.Equal(new[] { "old-access-token", "fresh-access-token" }, _client.SyncAccessTokensSeen.ToArray());
        // 刷新时传出去的 refresh token 必须是解密后的明文，不是库里的 "enc:…" 密文
        Assert.Equal("old-refresh-token", _client.RefreshedRefreshTokenSeen);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task Refreshed_Token_Is_Stored_Encrypted()
    {
        _client.SyncFaults[1] = Unauthorized();
        _client.RefreshReturns = new BitwardenRefreshResponse
        {
            AccessToken = "fresh-access-token",
            RefreshToken = "fresh-refresh-token",
        };

        await _authService.SyncNow(AccountId);

        var row = await _auth.Accounts.AsNoTracking().SingleAsync(a => a.Id == AccountId);
        Assert.StartsWith("enc:", row.AccessToken);          // 绝不落明文（AccountSecretsTests 同款红线）
        Assert.StartsWith("enc:", row.RefreshToken);

        var decoded = new AccountData { AccessToken = row.AccessToken, RefreshToken = row.RefreshToken };
        decoded.DecryptSensitiveFields(DbKey);
        Assert.Equal("fresh-access-token", decoded.AccessToken);
        Assert.Equal("fresh-refresh-token", decoded.RefreshToken);
    }

    [Fact]
    public async Task Retry_After_Refresh_Still_401_Says_Refresh_Failed_Too()
    {
        _client.SyncFaults[1] = Unauthorized();
        _client.SyncFaults[2] = Unauthorized();      // 刷新成功了，但服务器还是不认

        var ex = await Assert.ThrowsAsync<TamaException>(() => _authService.SyncNow(AccountId));

        Assert.Equal(2, _client.SyncCalls);          // 只重试一次，不无限循环
        Assert.Contains("401", ex.Message);
        Assert.Contains("自动刷新", ex.Message);
        Assert.Contains("重新登录", ex.Message);
    }

    [Fact]
    public async Task Refresh_Unavailable_Does_Not_Retry_But_Tells_User_To_Relogin()
    {
        _client.SyncFaults[1] = Unauthorized();
        _client.RefreshReturns = null;               // 没有 refresh token / 刷新被拒

        var ex = await Assert.ThrowsAsync<TamaException>(() => _authService.SyncNow(AccountId));

        Assert.Equal(1, _client.SyncCalls);          // 刷不出新 token 就别重试
        Assert.Contains("重新登录", ex.Message);
    }

    /// <summary>断网**不是**登录过期：不许去刷 token，也不许把原因说成"登录过期"。</summary>
    [Fact]
    public async Task Network_Failure_Is_Not_Treated_As_Expired_Token()
    {
        _client.SyncFaults[1] = new HttpRequestException("No such host is known.");

        var ex = await Assert.ThrowsAsync<TamaException>(() => _authService.SyncNow(AccountId));

        Assert.Equal(1, _client.SyncCalls);
        Assert.Null(_client.RefreshedRefreshTokenSeen);
        Assert.Contains("连不上", ex.Message);
    }

    /// <summary>畸形响应（缺 profile）的原话要传出来——那句话本身就是给人看的。</summary>
    [Fact]
    public async Task Malformed_Response_Message_Reaches_The_User()
    {
        _client.SyncFaults[1] = new InvalidOperationException("sync 响应缺少 profile（判定为畸形响应），已放弃本次同步以避免误删本地条目");

        var ex = await Assert.ThrowsAsync<TamaException>(() => _authService.SyncNow(AccountId));

        Assert.Contains("profile", ex.Message);
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
