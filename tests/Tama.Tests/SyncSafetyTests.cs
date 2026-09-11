using Tama.Core.Contracts;
using Tama.Core.Exceptions;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Auth;
using Tama.Services.Sync;
using Tama.Services.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 同步安全性的回归集合。这些用例守的都是"会静默丢数据"的东西：
///   1. 拉取**绝不能**覆盖本地还没推上去的改动（pending 与 failed 都算）；
///   2. 全量快照里没有的已同步条目要被清掉（服务端硬删之后本地不能留魂）；
///   3. 本地找不到条目时不能再"假装保存成功"；
///   4. 用户重新编辑要重置重试预算，失败条目不能被永久放弃。
/// </summary>
public class SyncSafetyTests : IDisposable
{
    private readonly SqliteConnection _vaultConn;
    private readonly SqliteConnection _authConn;
    private readonly TamaDbContext _vault;
    private readonly AuthDbContext _auth;
    private readonly AuthService _authService;
    private readonly FakeBitwarden _bitwarden = new();
    private const string AccountId = "acct-1";

    public SyncSafetyTests()
    {
        _vaultConn = new SqliteConnection("DataSource=:memory:");
        _vaultConn.Open();
        _authConn = new SqliteConnection("DataSource=:memory:");
        _authConn.Open();

        var vaultOptions = new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(_vaultConn).Options;
        var authOptions = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_authConn).Options;
        _vault = new TamaDbContext(vaultOptions);
        _auth = new AuthDbContext(authOptions);
        _vault.Database.EnsureCreated();
        _auth.Database.EnsureCreated();

        // AuthRefresh 自己不碰会话、密钥服务或 scope（密钥材料是 RefreshRequest 带进来的）；
        // 后台同步（解锁触发）不在这些用例的范围内，所以 scopeFactory/notifier 传空实现
        _authService = new AuthService(null!, _bitwarden, _auth, _vault, null!, Accounts(), null!, new VaultSyncNotifier(), new VaultSyncGate());
    }

    /// <summary>
    /// 真实的 BitwardenAccountService（测试库里没有任何账号 → FindBitwardenAccount 直接返回 null，
    /// 于是写入路径走"离线"分支；它不会碰 DatabaseKeyService，所以那个参数可以传 null）。
    /// </summary>
    private BitwardenAccountService Accounts() => new(_auth, null!, _bitwarden);

    private Cipher SeedLocal(string name, string syncStatus, string? pendingOp = null, int retryCount = 0)    {
        var c = new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Login,
            Name = name,
            SyncStatus = syncStatus,
            PendingOp = pendingOp,
            RetryCount = retryCount,
            Login = new CipherLogin { Username = "local-user", Password = "local-pw" },
        };
        _vault.Ciphers.Add(c);
        _vault.SaveChanges();
        return c;
    }

    private RefreshRequest Request() => new()
    {
        AccountId = AccountId,
        AccessToken = "token",
        DerivedEncKey = Convert.ToBase64String(new byte[32]),
        DerivedMacKey = Convert.ToBase64String(new byte[32]),
    };

    private static BitwardenSyncResponse FullSync(params BitwardenCipherResponse[] ciphers) => new()
    {
        IsFullSync = true,
        Ciphers = ciphers.ToList(),
        Folders = new(),
        Profile = new BitwardenProfileResponse { Id = "u1", Email = "u@example.com" },
        MaxRevisionDate = DateTime.UtcNow,
    };

    private static BitwardenCipherResponse ServerCipher(Guid id, string name, DateTime? deleted = null) => new()
    {
        Id = id.ToString(),
        Type = 1,
        Name = name,
        RevisionDate = DateTime.UtcNow,
        DeletedDate = deleted,
        Login = new BitwardenLoginData { Username = "server-user", Password = "server-pw" },
    };

    // ───────────────────────── 1. 拉取不许覆盖未推送的本地改动 ─────────────────────────

    [Fact]
    public async Task Refresh_Does_Not_Overwrite_Failed_Local_Edit()
    {
        // 这条是整套里最危险的一条：worker 重试超限把条目置成 failed 之后，
        // 旧代码只认 "pending" 做豁免 → 下一次拉取用服务器旧值把它整条盖掉。
        var local = SeedLocal("我本地改的名字", syncStatus: "failed", pendingOp: "update", retryCount: 9);
        _bitwarden.NextResponse = FullSync(ServerCipher(local.Id, "服务器上的旧名字"));

        await _authService.AuthRefresh(Request());

        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == local.Id);
        Assert.Equal("我本地改的名字", after.Name);
        Assert.Equal("local-user", after.Login!.Username);
        Assert.Equal("failed", after.SyncStatus);
        Assert.Equal("update", after.PendingOp);
    }

    [Fact]
    public async Task Refresh_Does_Not_Overwrite_Pending_Local_Edit()
    {
        var local = SeedLocal("本地待推送", syncStatus: "pending", pendingOp: "update");
        _bitwarden.NextResponse = FullSync(ServerCipher(local.Id, "服务器旧值"));

        await _authService.AuthRefresh(Request());

        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == local.Id);
        Assert.Equal("本地待推送", after.Name);
        Assert.Equal("pending", after.SyncStatus);
    }

    [Fact]
    public async Task Refresh_Does_Not_Delete_Pending_Row_That_Server_Marks_Deleted()
    {
        var local = SeedLocal("本地待推送", syncStatus: "failed", pendingOp: "update");
        _bitwarden.NextResponse = FullSync(ServerCipher(local.Id, "x", deleted: DateTime.UtcNow));

        await _authService.AuthRefresh(Request());

        Assert.True(await _vault.Ciphers.AnyAsync(c => c.Id == local.Id));
    }

    // ───────────────────────── 2. 已同步的条目以服务器为准 ─────────────────────────

    [Fact]
    public async Task Refresh_Overwrites_Synced_Row_From_Server()
    {
        var local = SeedLocal("旧名字", syncStatus: "synced");
        _bitwarden.NextResponse = FullSync(ServerCipher(local.Id, "服务器新名字"));

        await _authService.AuthRefresh(Request());

        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == local.Id);
        Assert.Equal("服务器新名字", after.Name);
        Assert.Equal("server-user", after.Login!.Username);
        Assert.Equal("synced", after.SyncStatus);
    }

    [Fact]
    public async Task Refresh_Removes_Synced_Orphan_Absent_From_Full_Snapshot()
    {
        // 服务端硬删（清空回收站）之后：全量快照里没有它 → 本地也要清掉。
        // 旧代码里 IsFullSync 永远是 false，这段清理从来没执行过。
        var orphan = SeedLocal("服务器上已经没有了", syncStatus: "synced");
        var kept = SeedLocal("还在", syncStatus: "synced");
        _bitwarden.NextResponse = FullSync(ServerCipher(kept.Id, "还在"));

        var r = await _authService.AuthRefresh(Request());

        Assert.False(await _vault.Ciphers.AnyAsync(c => c.Id == orphan.Id));
        Assert.True(await _vault.Ciphers.AnyAsync(c => c.Id == kept.Id));
        // 报告必须把孤儿清理算进"删除"。不算的话 UI 会显示"删除 0"而本地少了一批——是骗人的。
        Assert.Equal(1, r.Removed);
        Assert.Equal(0, r.Imported);
        Assert.Equal(1, r.Updated);
    }

    [Fact]
    public async Task Refresh_Keeps_Pending_Orphan()
    {
        // 本地刚建好还没推上去（服务器快照里当然没有）——绝不能被当孤儿删掉
        var fresh = SeedLocal("刚建的", syncStatus: "pending", pendingOp: "create");
        _bitwarden.NextResponse = FullSync();

        await _authService.AuthRefresh(Request());

        Assert.True(await _vault.Ciphers.AnyAsync(c => c.Id == fresh.Id));
    }

    [Fact]
    public async Task Refresh_Persists_Cursor_From_Server_Revision()
    {
        var cursor = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _bitwarden.NextResponse = FullSync();
        _bitwarden.NextResponse.MaxRevisionDate = cursor;

        await _authService.AuthRefresh(Request());

        var cache = await _auth.SyncCaches.AsNoTracking().SingleAsync(c => c.AccountId == AccountId);
        Assert.Equal(cursor, cache.SyncedAt);
    }

    // ───────────────────────── 3. 本地找不到条目：必须报错而不是静默成功 ─────────────────────────

    [Fact]
    public async Task Update_Throws_When_Local_Row_Missing()
    {
        var svc = new CipherService(_vault, Accounts(), _bitwarden);

        var ex = await Assert.ThrowsAsync<TamaException>(() => svc.Update(new UpdateCipherRequest
        {
            Id = Guid.NewGuid().ToString(),
            Type = 1,
            Name = "x",
        }));

        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public async Task Update_Resets_Retry_Budget_On_User_Edit()
    {
        // 库里有 Bitwarden 账号表但没有任何账号 → FindBitwardenAccount 返回 null，
        // 推送直接走"离线"分支，落成本地 pending
        var local = SeedLocal("旧", syncStatus: "failed", pendingOp: "update", retryCount: 9);
        var svc = new CipherService(_vault, Accounts(), _bitwarden);

        await svc.Update(new UpdateCipherRequest
        {
            Id = local.Id.ToString(),
            Type = 1,
            Name = "用户重新编辑过",
            Login = new CipherLoginRequest { Username = "u" },
        });

        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == local.Id);
        Assert.Equal("用户重新编辑过", after.Name);
        Assert.Equal("pending", after.SyncStatus);
        Assert.Equal(0, after.RetryCount);      // ← 新的推送意图，重试预算重来
        Assert.Null(after.LastAttempt);
    }

    // ───────────────────────── 4. 收藏：单独一条安全路径 ─────────────────────────

    /// <summary>
    /// 收藏**不能再走 Update（整条覆盖）**：页面手上的 CipherDto 里没有卡片/身份/自定义字段，
    /// 拿它去覆盖会把服务器上那些内容清空——一张拉下来的卡片被点个星星就变成空卡片。
    /// </summary>
    [Fact]
    public async Task SetFavorite_Toggles_Only_That_Flag_And_Keeps_Other_Sections()
    {
        var local = SeedLocal("卡片", syncStatus: "synced");
        local.Type = CipherType.Card;
        local.Card = new CipherCard { Number = "4111111111111111", Brand = "Visa" };
        local.Fields = new() { new CipherField { Id = 0, Name = "PIN", Value = "1234" } };
        await _vault.SaveChangesAsync();

        var svc = new CipherService(_vault, Accounts(), _bitwarden);
        await svc.SetFavorite(local.Id.ToString(), true);

        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == local.Id);
        Assert.True(after.Favorite);
        Assert.Equal("4111111111111111", after.Card!.Number);   // ← 卡片内容还在
        Assert.Equal("Visa", after.Card.Brand);
        Assert.Equal("PIN", after.Fields![0].Name);
        Assert.Equal(CipherType.Card, after.Type);
    }

    [Fact]
    public async Task SetFavorite_Is_Idempotent()
    {
        var local = SeedLocal("已经收藏了", syncStatus: "synced");
        local.Favorite = true;
        await _vault.SaveChangesAsync();

        var svc = new CipherService(_vault, Accounts(), _bitwarden);
        await svc.SetFavorite(local.Id.ToString(), true);

        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == local.Id);
        Assert.True(after.Favorite);
        Assert.Equal("synced", after.SyncStatus);   // 状态没变就不该被踢成 pending
    }

    [Fact]
    public async Task SetFavorite_Throws_When_Local_Row_Missing()
    {
        var svc = new CipherService(_vault, Accounts(), _bitwarden);
        await Assert.ThrowsAsync<TamaException>(() => svc.SetFavorite(Guid.NewGuid().ToString(), true));
    }

    // ───────────────────────── 5. 删除：没推上去过的东西不该去打扰服务器 ─────────────────────────

    /// <summary>
    /// 离线新建、从没推上去的条目（PendingOp == "create"）：服务器根本不知道它的存在，
    /// 所以删除只该删本地。旧写法会先推一次 delete（404）再落软删 + pending delete，
    /// 让 worker 永远重试一个不存在的 ID（实测：库里两条本地示例数据就是这么卡住的）。
    /// </summary>
    [Fact]
    public async Task Delete_Of_Never_Pushed_Local_Cipher_Is_Local_Only()
    {
        var local = SeedLocal("离线新建", syncStatus: "pending", pendingOp: "create");
        var svc = new CipherService(_vault, Accounts(), _bitwarden);

        await svc.Delete(local.Id.ToString());

        // 硬删（行没了）。若走的是旧的"软删 + pending delete"，行还在（只是 DeletedAt 有值）。
        Assert.False(await _vault.Ciphers.AnyAsync(c => c.Id == local.Id));
    }

    /// <summary>对照组：已经在服务器上的条目删除仍走原有语义（软删 + pending，交给 worker 推）</summary>
    [Fact]
    public async Task Delete_Of_Synced_Cipher_Still_Soft_Deletes_And_Queues()
    {
        var local = SeedLocal("服务器上有的", syncStatus: "synced");
        var svc = new CipherService(_vault, Accounts(), _bitwarden);

        await svc.Delete(local.Id.ToString());

        var after = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == local.Id);
        Assert.NotNull(after.DeletedAt);
        Assert.Equal("pending", after.SyncStatus);
        Assert.Equal("delete", after.PendingOp);
    }

    // ───────────────────────── 6. 退避策略 ─────────────────────────

    [Fact]
    public void Retry_Backoff_Grows_And_Caps()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), SyncBackoff.Delay(0));
        Assert.Equal(TimeSpan.FromSeconds(10), SyncBackoff.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(20), SyncBackoff.Delay(2));
        Assert.Equal(TimeSpan.FromHours(1), SyncBackoff.Delay(20));
    }

    [Fact]
    public void Failed_Row_Is_Retried_After_Backoff_Not_Forever_Skipped()
    {
        var now = DateTime.UtcNow;
        var c = new Cipher { SyncStatus = "failed", RetryCount = 3, LastAttempt = now.AddSeconds(-1) };

        Assert.False(SyncBackoff.IsDue(c, now));                 // 刚失败过：等退避
        Assert.True(SyncBackoff.IsDue(c, now.AddSeconds(41)));   // 退避到点：继续试
        Assert.True(SyncBackoff.IsDue(new Cipher { SyncStatus = "failed" }, now)); // 从没试过
    }

    public void Dispose()
    {
        _vault.Dispose();
        _auth.Dispose();
        _vaultConn.Dispose();
        _authConn.Dispose();
    }

    /// <summary>只实现被测路径需要的 SyncAsync，其余成员一律抛（xunit 项目不带 mock 框架）。</summary>
    private sealed class FakeBitwarden : IBitwardenApiClient
    {
        public BitwardenSyncResponse NextResponse { get; set; } = new();

        public Task<BitwardenSyncResponse> SyncAsync(string accessToken, string encryptionKey, DateTime? lastSync = null)
            => Task.FromResult(NextResponse);

        public Task<BitwardenSyncResponse> SyncAsync(string accessToken, string encKey, string macKey, DateTime? lastSync = null)
            => Task.FromResult(NextResponse);

        public Task<BitwardenLoginResponse> LoginAsync(
            string email,
            string masterPassword,
            string? twoFactorCode = null,
            int? twoFactorProvider = null,
            bool newDeviceVerification = false) => throw new NotSupportedException();
        public Task<List<BitwardenCipherResponse>> GetCiphersAsync(string accessToken, string encryptionKey) => throw new NotSupportedException();
        public Task<BitwardenRefreshResponse?> RefreshTokenAsync(string refreshToken, string? accessToken = null) => throw new NotSupportedException();
        public Task<BitwardenCipherResponse?> CreateCipherAsync(string accessToken, string encKeyB64, string macKeyB64, object cipherRequest) => throw new NotSupportedException();
        public Task<bool> UpdateCipherAsync(string accessToken, string cipherId, object cipherRequest) => throw new NotSupportedException();
        public Task<bool> DeleteCipherAsync(string accessToken, string cipherId) => throw new NotSupportedException();
        public Task<bool> TrashCipherAsync(string accessToken, string cipherId) => throw new NotSupportedException();
        public Task<bool> RestoreCipherAsync(string accessToken, string cipherId) => throw new NotSupportedException();
        public Task<BitwardenFolderResponse?> CreateFolderAsync(string accessToken, string name, byte[] encKey, byte[] macKey) => throw new NotSupportedException();
        public Task<bool> UpdateFolderAsync(string accessToken, string folderId, string name, byte[] encKey, byte[] macKey) => throw new NotSupportedException();
        public Task<bool> DeleteFolderAsync(string accessToken, string folderId) => throw new NotSupportedException();
    }
}
