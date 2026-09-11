using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 账号敏感字段（access token / refresh token / 派生密钥）**在库里必须始终是密文**。
///
/// 这套用例来自一个真实存在的漏洞：<c>DecryptSensitiveFields</c> 是**就地**把字段替换成明文的，
/// 而被跟踪的实体只要遇上同一个 DbContext 上的任意一次 SaveChanges 就会被明文写回库。
/// 那个 DbContext 是 Scoped 的，调用方（AuthRefresh 结尾、FolderService 更新 SyncCache…）
/// 紧接着就会 SaveChanges —— 于是"同步一次 = token 与派生密钥明文落盘"。
///
/// 两条约束：① 只读取密钥的路径（FindBitwardenAccount）必须 AsNoTracking；
/// ② 确实要写回去的路径（TryRefreshToken）必须自己加密后再存。
/// </summary>
public class AccountSecretsTests : IDisposable
{
    private const string AccountId = "acc-1";

    private static readonly byte[] DbKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] EncKey = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] MacKey = Enumerable.Range(64, 32).Select(i => (byte)i).ToArray();

    private readonly SqliteConnection _authConn;
    private readonly AuthDbContext _auth;
    private readonly FakeBitwardenClient _client = new();
    private readonly DatabaseKeyService _keyService = new();
    private readonly BitwardenAccountService _accounts;

    public AccountSecretsTests()
    {
        _authConn = new SqliteConnection("DataSource=:memory:");
        _authConn.Open();
        _auth = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_authConn).Options);
        _auth.Database.EnsureCreated();

        _keyService.SetKey(DbKey);
        _accounts = new BitwardenAccountService(_auth, _keyService, _client);
    }

    private void LinkAccount()
    {
        var account = new AccountData
        {
            Id = AccountId,
            Email = "me@example.com",
            Type = "bitwarden",
            ServerUrl = "https://vault.bitwarden.com",
            AccessToken = "plain-access-token",
            RefreshToken = "plain-refresh-token",
            EncryptionKey = "user-key",
            DerivedEncKey = Convert.ToBase64String(EncKey),
            DerivedMacKey = Convert.ToBase64String(MacKey),
        };
        account.EncryptSensitiveFields(DbKey);
        _auth.Accounts.Add(account);
        _auth.SaveChanges();
    }

    [Fact]
    public async Task Finding_The_Account_Does_Not_Write_Plaintext_Back()
    {
        LinkAccount();

        var keys = await _accounts.FindBitwardenAccount(null);
        Assert.NotNull(keys);
        Assert.Equal("plain-access-token", keys!.Value.accessToken);

        // 调用方紧接着就会落库（AuthRefresh 的 SyncCache 更新、FolderService 都是这么做的）
        await _auth.SaveChangesAsync();

        var after = await _auth.Accounts.AsNoTracking().SingleAsync();
        Assert.StartsWith("enc:", after.AccessToken);
        Assert.StartsWith("enc:", after.DerivedEncKey);
        Assert.StartsWith("enc:", after.DerivedMacKey);
    }

    [Fact]
    public async Task Token_Refresh_Decrypts_Before_Use_And_Reencrypts_Before_Saving()
    {
        LinkAccount();

        var token = await _accounts.TryRefreshToken(AccountId);

        Assert.Equal("new-access-token", token);
        // 传给服务器/身份端的必须是**明文** token。以前这里不解密、能跑通纯属巧合
        // （靠 FindBitwardenAccount 先把同一个被跟踪实例解成明文）。
        Assert.Equal("plain-refresh-token", _client.RefreshedRefreshTokenSeen);

        var after = await _auth.Accounts.AsNoTracking().SingleAsync();
        Assert.StartsWith("enc:", after.RefreshToken);
        Assert.StartsWith("enc:", after.AccessToken);
    }

    public void Dispose()
    {
        _auth.Dispose();
        _authConn.Dispose();
    }
}
