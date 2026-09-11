using System.Text.Json;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Bitwarden;
using Tama.Services.Sync;
using Tama.Services.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

using Tama.Core.Exceptions;

namespace Tama.Services.Auth;

/// <summary>
/// 账号与会话：主密码 setup / unlock / lock、Bitwarden 登录、多账号管理。
/// 实现 <see cref="IAuthApi"/>；页面与宿主直接注入该接口（进程内强类型，无 JSON）。
/// </summary>
public class AuthService : IAuthApi
{
    private readonly IAuthSession _authSession;
    private readonly IBitwardenApiClient _bitwarden;
    private readonly AuthDbContext _db;
    private readonly TamaDbContext _dbVault;
    private readonly DatabaseKeyService _dbKeyService;
    private readonly BitwardenAccountService _accounts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVaultSyncNotifier _syncNotifier;
    private readonly VaultSyncGate _syncGate;
    private static readonly ILogger Log = Serilog.Log.ForContext<AuthService>();

    public AuthService(
        IAuthSession authSession,
        IBitwardenApiClient bitwarden,
        AuthDbContext db,
        TamaDbContext dbVault,
        DatabaseKeyService dbKeyService,
        BitwardenAccountService accounts,
        IServiceScopeFactory scopeFactory,
        IVaultSyncNotifier syncNotifier,
        VaultSyncGate syncGate)
    {
        _authSession = authSession;
        _bitwarden = bitwarden;
        _db = db;
        _dbVault = dbVault;
        _dbKeyService = dbKeyService;
        _accounts = accounts;
        _scopeFactory = scopeFactory;
        _syncNotifier = syncNotifier;
        _syncGate = syncGate;
    }

    public async Task<AuthStatusResponse> Status()
    {
        // 每次从磁盘刷新 IsSetup：MAUI 在 App.xaml.cs 启动时刷过一次，
        // 但 Tama.Api（浏览器模式）没有这个入口，重启后不刷新会导致
        // isSetup 永远为 false、解锁页永远进不去
        await _authSession.IsSetupAsync();
        return new AuthStatusResponse(
            IsSetup: _authSession.IsSetup,
            IsUnlocked: _authSession.IsUnlocked,
            // 72h 主密码重验证策略：超时后指纹解锁被拒绝，前端隐藏指纹按钮并要求主密码
            BioUnlockAllowed: !_authSession.RequireMasterPasswordRecheck());
    }

    public async Task Setup(SetupRequest req)
    {
        if (req == null || req.Password.Length < 6)
            throw new TamaException("Password must be at least 6 characters");
        if (_authSession.IsSetup)
            throw new TamaException("Vault already set up");

        await _authSession.SetupAsync(req.Password);
    }

    public async Task Unlock(UnlockRequest req)
    {
        if (req == null) throw new TamaException("Invalid request");
        if (!_authSession.IsSetup) throw new TamaException("Vault not set up");

        var success = await _authSession.UnlockAsync(req.Password);
        if (!success) throw new TamaException("Invalid password");

        // 解锁即尝试同步（事件触发）：用户不该需要记得去点账号页那个按钮。
        StartBackgroundSync();
    }

    /// <summary>
    /// 解锁后**后台**拉一次云端库。
    ///
    /// 刻意不 await、也不向调用方抛异常，两条理由：
    ///   1) 解锁不能被网络拖住（本机实测 198 条要几秒，网络差更久）；
    ///   2) 同步失败只意味着"数据旧一点"，绝不能连累"用户已经进来了"这件事。
    /// 跑完通过 <see cref="IVaultSyncNotifier"/> 广播，页面据此重读本地数据。
    /// 服务都是 Scoped，而这条任务活得比本次调用久 → 必须自己开 scope 拿一套新的。
    ///
    /// 防重入**不在这里**（以前是这里的一个实例字段，只能挡住同一个电路里的重复解锁）：
    /// 统一闸门是进程级的 <see cref="VaultSyncGate"/>，在 <see cref="AuthRefresh"/> 里。
    /// </summary>
    private void StartBackgroundSync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var accounts = scope.ServiceProvider.GetRequiredService<BitwardenAccountService>();
                var keys = await accounts.FindBitwardenAccount(null);
                if (keys == null)
                {
                    Log.Debug("Unlock sync skipped: no linked Bitwarden account");
                    return;
                }

                var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
                var result = await auth.SyncNow(keys.Value.accountId);
                if (result.Busy)
                {
                    // 另一次拉取（定时轮询 / 用户手动同步）正在跑，它会自己广播结果
                    Log.Information("Unlock sync skipped: another vault sync is already running");
                    return;
                }
                Log.Information("Unlock sync done: {Imported} imported, {Updated} updated, {Removed} removed, {Skipped} skipped",
                    result.Imported, result.Updated, result.Removed, result.Skipped.Count);
                _syncNotifier.Raise(result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Unlock sync failed (unlock already succeeded, vault stays usable)");
            }
        });
    }

    public void Lock() => _authSession.Lock();

    public async Task<BitwardenLoginResponse> Login(LoginRequest req)
    {
        if (req == null) throw new TamaException("Invalid request");
        if (!_authSession.IsUnlocked) throw new TamaException("Vault is locked");

        return await _bitwarden.LoginAsync(
            req.Email,
            req.MasterPassword,
            req.TwoFactorCode,
            req.TwoFactorProvider,
            req.NewDeviceVerification);
    }

    // === 拉取同步（AuthRefresh） ===
    //
    // 这是**唯一**把 Bitwarden 服务端密码库落进本地表的实现，两个入口（2026-09-12 接上）：
    //   · 账号页「立即同步」→ SyncNow(accountId)
    //   · 解锁成功后 StartBackgroundSync()（不 await，失败不连累解锁）
    // （更早的注释写"没有任何 UI 入口"——那是 Blazor 迁移期的事实，已经过期。）
    //   同批的另外五个方法（AuthSync / AuthGetCache / AuthGetToken / AuthUpdateToken /
    //   AuthRefreshAccessToken）经核对是纯冗余，已删除；这一个不是：
    //     · AuthRefreshAccessToken 与 BitwardenAccountService.TryRefreshToken 完全重复
    //     · AuthSync 只是转发一次 SyncAsync，结果连库都不落
    //     · AuthGetCache 读本地表，而 CipherService.Search 已经直接读同一批表
    //     · AuthGetToken 的解密取账号逻辑与 BitwardenAccountService.FindBitwardenAccount 重复
    //     · AuthUpdateToken 无调用方，refresh 路径已由 TryRefreshToken 覆盖
    //   要"精简"这一个，只有两条路：接到 UI 的"立即同步"入口，或确认不要云端拉取后整段删。

    /// <summary>
    /// 从服务器拉取整个密码库并落进本地表（**唯一**一条"下载"方向的实现）。
    ///
    /// 三个入口：UI 走 <see cref="SyncNow"/>（只要账号 Id，密钥材料由本服务自己解析）；
    /// 解锁后的后台同步；后台定时轮询（<c>SyncWorker</c>）。这个重载直接吃密钥材料，
    /// 方便测试与定时同步。
    ///
    /// 进程级的 <see cref="VaultSyncGate"/> 就在这里：同一时刻只允许一次全量拉取，
    /// 拿不到闸门直接返回 <see cref="VaultSyncResult.None"/>（等一个正在跑的同步没有意义）。
    /// </summary>
    public async Task<VaultSyncResult> AuthRefresh(RefreshRequest req)
    {
        if (req == null) throw new TamaException("Invalid request");

        if (!_syncGate.TryEnter())
        {
            Log.Information("A vault sync is already running, skipping this request");
            return VaultSyncResult.AlreadyRunning;
        }
        try
        {
            return await AuthRefreshCoreAsync(req);
        }
        finally
        {
            _syncGate.Exit();
        }
    }

    private async Task<VaultSyncResult> AuthRefreshCoreAsync(RefreshRequest req)
    {
        var syncWatch = System.Diagnostics.Stopwatch.StartNew();

        // SyncCache.SyncedAt **不是**服务端游标，也决定不了"下次从哪取"——Bitwarden 的 /api/sync
        // 没有增量参数，每次都给全量快照（见 BitwardenApiClient.FetchAndDecryptSyncAsync 的注释）。
        // 它现在的唯一作用是"游标下限"：让下一次 maxRevisionDate 不会比上一次更小（防回退）。
        // SyncCache 的另一个身份是**上一份 payload 的缓存**（Passkey/Watchtower/FolderService 读它）。
        var existingCache = await _db.SyncCaches.AsNoTracking().FirstOrDefaultAsync(c => c.AccountId == req.AccountId);
        var lastSync = existingCache?.SyncedAt;

        var (result, failure) = await TrySyncAsync(req, lastSync);

        // Bitwarden 的 access token 是**会过期**的（默认 1 小时），而库里存的是登录那一刻拿到的那一个。
        // 过期之后每一次同步都是 401，而在此之前**没有任何地方会去刷新它**
        // （BitwardenAccountService.TryRefreshToken 只有测试在调用）——用户实测到的现象就是
        // "同步失败：All key approaches failed"，而唯一的出路是重新登录。
        // 这里：只在**确认是 401** 时刷新一次、重试一次（别把断网/畸形响应也当成过期），且只重试一次。
        if (result == null && IsUnauthorized(failure))
        {
            var refreshed = await _accounts.TryRefreshToken(req.AccountId);
            if (refreshed != null)
            {
                Log.Information("Access token was rejected (401); refreshed it, retrying the vault sync once");
                req = req with { AccessToken = refreshed };
                (result, failure) = await TrySyncAsync(req, lastSync);
            }
        }

        if (result == null)
        {
            // ⚠ 必须 Log.Error：Release 的文件 sink 只收 Error 及以上，而以前这里只留了一句 Warning
            //   （TrySyncAsync 里）→ 界面上一句看不懂的英文，日志里一片空白，等于没有线索。
            Log.Error(failure, "Vault sync failed: {Reason}", DescribeSyncFailure(failure));
            throw new TamaException(DescribeSyncFailure(failure));
        }        // 同步响应里只可能带 profile/org keys；org cipher 解密失败通常意味着密钥轮换。
        // 注意：现在每次都是全量拉取（Bitwarden 只有全量语义），所以"再全量拉一次"没有意义
        // ——同样的 payload 会得到同样的结果。这里只如实记日志，不假装补救。
        if (result.DecryptFailures > 0)
            Log.Warning("Sync had {Failures} org cipher decrypt failures (org key rotation?)", result.DecryptFailures);

        var imported = 0;
        var updated = 0;
        var removed = 0;
        var unchanged = 0;
        var skipped = new List<string>();

        // 写入本地 Ciphers 表
        var serverCipherIds = new HashSet<Guid>();
        foreach (var serverCipher in result.Ciphers)
        {
            if (!Guid.TryParse(serverCipher.Id, out var serverGuid))
            {
                Log.Warning("Skipping cipher with non-GUID id: {Id}", serverCipher.Id);
                continue;
            }
            serverCipherIds.Add(serverGuid);

            if (serverCipher.DeletedDate.HasValue)
            {
                // 服务器已删除 → 本地也删除，但**只删"已同步"的**：
                // pending/failed 都意味着本地有还没推上去的改动，绝不能被服务端状态抹掉
                var existing = await _dbVault.Ciphers.FindAsync(serverGuid);
                if (existing != null && existing.SyncStatus == "synced")
                {
                    _dbVault.Ciphers.Remove(existing);
                    removed++;
                }
                continue;
            }

            var cipherId = serverGuid;
            var local = await _dbVault.Ciphers.FindAsync(cipherId);

            if (local != null && local.SyncStatus != "synced")
            {
                // 本地有还没推上去的改动（pending 排队中，或 failed 已经重试到放弃）→ 整条跳过。
                // ⚠ 这里必须是 != "synced" 而不是 == "pending"：写成只认 pending 的话，
                //   worker 重试 5 次后置成 failed 的条目会被下一次拉取**静默用服务器旧值覆盖**，
                //   用户改了很多次没推上去的那次修改就此消失（而且 UI 上什么都看不出来）。
                continue;
            }

            // 一行都没变的条目不必重写。RevisionDate 是 Bitwarden 的"内容版本号"，
            // 而 ApplyRemote 每次都把它落在本地的 UpdatedAt 上 —— 两者相等就意味着
            // 服务器上那份与本地这份是同一个版本，重写一遍只是把同样的值再写一次。
            // 实测这一次全表重写要 162–507ms（198 条），所以省下来的正是这部分；
            // 附带好处：Updated 从"每次都 198"变成**真实的变化计数**，定时轮询据此决定要不要提示用户。
            // ⚠ 这条跳过的前提是"已同步的行与服务器一致"（本地有改动一定是 pending/failed，
            //   上面那条豁免已经把它们挑走了）。比较不相等时照旧走重写，所以宁可比不出来也不会写坏。
            // ⚠⚠ 但"同一个 RevisionDate"**不等于"本地这份不缺东西"**：本地那份是哪个版本的代码写的
            //   是另一件事。旧版本的内联映射只填 Login/Notes（卡片/自定义字段/通行密钥全丢），
            //   而跳过逻辑与 CipherMapper 是同一个提交进来的 → 老库从此永远修不好
            //   （实测症状：主页面「通行密钥」筛选恒为 0、点不动）。所以这里再问一句
            //   LocalIsMissingData：缺了服务器有的段落就照旧重写一遍。
            if (local != null && local.UpdatedAt == serverCipher.RevisionDate
                && !CipherMapper.LocalIsMissingData(local, serverCipher))
            {
                unchanged++;
                continue;
            }

            // 本地模型装不下的条目（身份信息的多段地址等）不拉：拉下来之后任何一次编辑/收藏
            // 都会按本地那副残缺形状推回服务器，等于静默压扁用户数据。跳过并如实报数。
            if (!CipherMapper.CanStoreLocally(serverCipher))
            {
                skipped.Add(serverCipher.Name ?? serverCipher.Id);
                Log.Warning("Skipped cipher {Id} ({Name}): {Reason}",
                    serverCipher.Id, serverCipher.Name, CipherMapper.DescribeSkip(serverCipher));
                continue;
            }

            // 四种类型 + 自定义字段 + 通行密钥的映射全在 CipherMapper 里（此前这里只填 Login）
            var cipher = local ?? new Cipher { Id = cipherId };
            CipherMapper.ApplyRemote(cipher, serverCipher);
            if (local == null)
            {
                _dbVault.Ciphers.Add(cipher);
                imported++;
            }
            else
            {
                updated++;
            }
        }

        // 清理孤儿：服务器完整快照里没有的本地条目（已在服务器上硬删/移出组织）。
        // IsFullSync 由 API 客户端给，含义是"这份 payload 是完整快照"；现在每次同步都是全量，
        // 所以这里每次都会跑通——这正是修掉"服务端删掉的条目永远留在本地"的地方。
        // 只删 SyncStatus == "synced" 的，本地未推送的改动一律不动。
        if (result.IsFullSync)
        {
            var orphans = await _dbVault.Ciphers
                .Where(c => !serverCipherIds.Contains(c.Id) && c.SyncStatus == "synced")
                .ToListAsync();
            if (orphans.Count > 0)
            {
                _dbVault.Ciphers.RemoveRange(orphans);
                removed += orphans.Count;   // 也算"删除"：不然报告写"删除 0"而本地其实少了一批，是骗人的
                Log.Information("Removed {Count} orphan ciphers absent from server snapshot (full sync)", orphans.Count);
            }
        }

        // 写入本地 Folders 表。sync 响应的 folders 字段始终全量（服务器行为），
        // 因此"服务器没有的本地文件夹 → 删除"在任何同步下都安全；跳过 pending（离线创建未推送）
        var serverFolderIds = new HashSet<Guid>();
        foreach (var serverFolder in result.Folders)
        {
            if (!Guid.TryParse(serverFolder.Id, out var serverFolderGuid))
            {
                Log.Warning("Skipping folder with non-GUID id: {Id}", serverFolder.Id);
                continue;
            }
            serverFolderIds.Add(serverFolderGuid);
            var existing = await _dbVault.Folders.FindAsync(serverFolderGuid);
            if (existing == null)
            {
                _dbVault.Folders.Add(new Folder
                {
                    Id = serverFolderGuid,
                    Name = serverFolder.Name,
                    SyncStatus = "synced",
                });
            }
            else if (existing.SyncStatus != "pending")
            {
                existing.Name = serverFolder.Name;
                existing.SyncStatus = "synced";
            }
        }

        var orphanFolders = await _dbVault.Folders
            .Where(f => !serverFolderIds.Contains(f.Id) && f.SyncStatus != "pending")
            .ToListAsync();
        if (orphanFolders.Count > 0)
        {
            _dbVault.Folders.RemoveRange(orphanFolders);
            Log.Information("Removed {Count} folders absent from server sync", orphanFolders.Count);
        }

        await _dbVault.SaveChangesAsync();
        // 刷新 SyncCache：① 存下这份 payload 供 Passkey/Watchtower/FolderService 复用；
        // ② 记一个**防回退**的 SyncedAt（服务器 revisionDate 的最大值，落成本地时钟也无妨，
        //    因为它只作为下次 maxRevisionDate 的下限，不决定"从哪里开始拉"）。
        var nextCursor = result.MaxRevisionDate ?? lastSync ?? DateTime.UtcNow;
        if (existingCache == null)
        {
            var newCache = new SyncCache { AccountId = req.AccountId };
            _db.SyncCaches.Add(newCache);
            newCache.CiphersJson = JsonSerializer.Serialize(result.Ciphers);
            newCache.FoldersJson = JsonSerializer.Serialize(result.Folders);
            newCache.SyncedAt = nextCursor;
        }
        else
        {
            var trackedCache = await _db.SyncCaches.FindAsync(existingCache.AccountId);
            if (trackedCache != null)
            {
                trackedCache.CiphersJson = JsonSerializer.Serialize(result.Ciphers);
                trackedCache.FoldersJson = JsonSerializer.Serialize(result.Folders);
                trackedCache.SyncedAt = nextCursor;
            }
        }

        // Watchtower 缓存随同步失效：报告要按条目算（HIBP 要发网络请求），
        // **只在这次同步真的改了东西时**才丢掉它 —— 定时轮询每 10 分钟拉一次，
        // 若无条件失效，用户每次打开"安全卫士"都要重算一遍整库，慢得莫名其妙。
        if (imported + updated + removed > 0)
        {
            var wtCache = await _db.WatchtowerReports.FindAsync(req.AccountId);
            if (wtCache != null)
            {
                _db.WatchtowerReports.Remove(wtCache);
            }
        }

        await _db.SaveChangesAsync();

        Log.Information("Vault sync done in {Ms}ms: {Imported} imported, {Updated} updated, {Removed} removed, {Unchanged} unchanged, {Skipped} skipped",
            syncWatch.ElapsedMilliseconds, imported, updated, removed, unchanged, skipped.Count);

        return new VaultSyncResult(imported, updated, removed, skipped);
    }

    /// <summary>
    /// UI 的"立即同步"入口（账号页）。密钥材料由本服务从账号里自己解析——
    /// 让页面去拼 AccessToken/DerivedEncKey 是不该有的形状（契约里不该出现密钥材料）。
    /// </summary>
    public async Task<VaultSyncResult> SyncNow(string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) throw new TamaException("Invalid request");

        var keys = await _accounts.FindBitwardenAccount(accountId)
            ?? throw new TamaException("账号不存在或缺少密钥材料，请重新登录该账号");

        return await AuthRefresh(new RefreshRequest
        {
            AccountId = keys.accountId,
            AccessToken = keys.accessToken,
            DerivedEncKey = Convert.ToBase64String(keys.encKey),
            DerivedMacKey = Convert.ToBase64String(keys.macKey),
        });
    }

    /// <summary>
    /// 依次尝试 derived keys / encryptionKey 两种同步路径，返回第一个成功的结果。
    ///
    /// **失败原因必须往上传**：以前这里把异常吞成一句 `Log.Warning`，调用方只能报
    /// "All key approaches failed" —— 401（token 过期）/ 断网 / 响应畸形在界面上长得一模一样，
    /// 而 Release 的文件日志只收 Error 及以上，连那句 Warning 都看不到。
    /// </summary>
    private async Task<(BitwardenSyncResponse? result, Exception? failure)> TrySyncAsync(RefreshRequest req, DateTime? lastSync)
    {
        Exception? failure = null;

        if (!string.IsNullOrEmpty(req.DerivedEncKey) && !string.IsNullOrEmpty(req.DerivedMacKey))
        {
            try
            {
                var ok = await _bitwarden.SyncAsync(req.AccessToken, req.DerivedEncKey, req.DerivedMacKey, lastSync);
                return (ok, null);
            }
            catch (Exception ex)
            {
                failure = ex;
                Log.Warning(ex, "Derived keys sync failed");
            }
        }

        if (!string.IsNullOrEmpty(req.EncryptionKey))
        {
            try
            {
                var ok = await _bitwarden.SyncAsync(req.AccessToken, req.EncryptionKey, lastSync);
                return (ok, null);
            }
            catch (Exception ex)
            {
                failure = ex;
                Log.Warning(ex, "EncryptionKey sync failed");
            }
        }

        return (null, failure);
    }

    /// <summary>是不是"登录状态过期"（401）。HttpClient 的 EnsureSuccessStatusCode 会把状态码带在异常上。</summary>
    private static bool IsUnauthorized(Exception? ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized };

    /// <summary>给用户看的一句话（直接显示在账号页上）：401 / 断网 / 响应畸形不能长得一样。</summary>
    private static string DescribeSyncFailure(Exception? ex) => ex switch
    {
        null => "账号缺少可用的密钥材料，请重新登录这个 Bitwarden 账号",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } =>
            "登录状态已过期（HTTP 401），自动刷新也没成功——请重新登录这个 Bitwarden 账号",
        HttpRequestException { StatusCode: { } status } =>
            $"服务器拒绝了这次同步（HTTP {(int)status}）",
        HttpRequestException => "连不上 Bitwarden 服务器（网络或代理问题）",
        JsonException => "服务器返回的内容解析不了（不是 Bitwarden 官方服务端？）：" + ex.Message,
        _ => "同步失败：" + ex.Message,
    };

    public async Task<List<AccountSummaryDto>> Accounts()
    {
        var accounts = await _db.Accounts.AsNoTracking().ToListAsync();
        return accounts.Select(a => new AccountSummaryDto(
            a.Id, a.Email, a.Type, a.ServerUrl,
            !string.IsNullOrEmpty(a.AccessToken),
            a.CreatedAt)).ToList();
    }

    public async Task<AuthAddAccountResponse> AddAccount(AddAccountRequest req)
    {
        if (req == null) throw new TamaException("Invalid request");

        var account = new AccountData
        {
            Id = Guid.NewGuid().ToString(),
            Email = req.Email,
            Type = req.Type,
            ServerUrl = req.ServerUrl,
            AccessToken = req.AccessToken,
            RefreshToken = req.RefreshToken,
            EncryptionKey = req.EncryptionKey,
            RawTokenKey = req.RawTokenKey,
            DerivedEncKey = req.DerivedEncKey,
            DerivedMacKey = req.DerivedMacKey,
            KdfIterations = req.KdfIterations,
        };
        account.EncryptSensitiveFields(_dbKeyService.GetKey());
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();
        return new AuthAddAccountResponse(account.Id);
    }

    public async Task DeleteAccount(string accountId)
    {
        var account = await _db.Accounts.FindAsync(accountId);
        if (account == null) throw new TamaException("Account not found");

        _db.Accounts.Remove(account);
        var caches = await _db.SyncCaches.Where(c => c.AccountId == accountId).ToListAsync();
        _db.SyncCaches.RemoveRange(caches);
        await _db.SaveChangesAsync();
    }
}
