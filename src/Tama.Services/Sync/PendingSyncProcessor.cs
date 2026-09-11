using Microsoft.EntityFrameworkCore;
using Serilog;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Bitwarden;
using Tama.Services.Vault;

namespace Tama.Services.Sync;

/// <summary>一轮推送的结果（只用于日志与测试断言）。</summary>
public sealed record PendingSyncOutcome(int Ciphers, int Folders)
{
    public static readonly PendingSyncOutcome None = new(0, 0);
    public int Total => Ciphers + Folders;
}

/// <summary>
/// 离线写入队列的**消费者**：把本地标了 pending/failed 的行推给 Bitwarden。
///
/// 队列不是一张表——它就是"带 pending/failed 标记的那些行"这个逻辑集合（见 Cipher 上那三列的注释）。
/// 这个类负责跑那条 WHERE，条目与文件夹**两边都要跑**（文件夹以前没人跑，于是离线建的文件夹
/// 永远只存在本机）。
///
/// 从 <see cref="SyncWorker"/> 里抽出来的理由：worker 是 BackgroundService，用例没法优雅地驱动它的
/// while 循环，而这里做的事（捞候选 → 按退避筛 → 推 → 回写状态/主键）全是纯输入输出的。
/// </summary>
public class PendingSyncProcessor
{
    /// <summary>一次最多看多少条候选（按 LastAttempt 排序）。</summary>
    private const int CandidatePool = 50;

    /// <summary>一轮最多真推几条。5 秒一轮，一次推太多只会在网络差时堆在一起。</summary>
    private const int BatchSize = 10;

    /// <summary>失败多少次之后把状态显性化成 failed（**不代表放弃**，见 MarkFailed）。</summary>
    private const int FailedAfterRetries = 5;

    private readonly TamaDbContext _dbVault;
    private readonly AuthDbContext _db;
    private readonly IBitwardenApiClient _bitwarden;
    private readonly DatabaseKeyService _keyService;
    private readonly FolderService _folders;
    private static readonly ILogger Log = Serilog.Log.ForContext<PendingSyncProcessor>();

    public PendingSyncProcessor(
        TamaDbContext dbVault,
        AuthDbContext db,
        IBitwardenApiClient bitwarden,
        DatabaseKeyService keyService,
        FolderService folders)
    {
        _dbVault = dbVault;
        _db = db;
        _bitwarden = bitwarden;
        _keyService = keyService;
        _folders = folders;
    }

    /// <summary>跑一轮。没有账号 / 没有待推送的东西时是廉价的空转。</summary>
    public async Task<PendingSyncOutcome> RunOnceAsync(CancellationToken ct = default)
    {
        // ⚠ 账号必须 AsNoTracking：DecryptSensitiveFields 是**就地**把字段换成明文的，
        //   被跟踪的实体一旦遇上任意一次 SaveChanges（本方法要写 SyncCache！）就会被**明文写回库**。
        //   （这个坑以前就存在：AuthRefresh 结尾的 SaveChanges 会把 token 明文落库。）
        var account = await _db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Type == "bitwarden" && !string.IsNullOrEmpty(a.DerivedEncKey), ct);
        if (account == null) return PendingSyncOutcome.None;

        byte[] encKey, macKey;
        try
        {
            account.DecryptSensitiveFields(_keyService.GetKey());
            encKey = Convert.FromBase64String(account.DerivedEncKey ?? string.Empty);
            macKey = Convert.FromBase64String(account.DerivedMacKey ?? string.Empty);
        }
        catch (Exception ex)
        {
            // 密钥材料坏了（没解锁就调用、或库被人动过）不该炸掉整个后台循环
            Log.Error(ex, "Cannot resolve Bitwarden keys, skipping this pending-sync round");
            return PendingSyncOutcome.None;
        }

        var now = DateTime.UtcNow;
        var ciphers = await PushCiphersAsync(account, encKey, macKey, now, ct);
        var folders = await PushFoldersAsync(account, encKey, macKey, now, ct);

        await _dbVault.SaveChangesAsync(ct);
        await _db.SaveChangesAsync(ct);

        if (ciphers + folders > 0)
            Log.Information("Pending sync round done: {Ciphers} ciphers, {Folders} folders", ciphers, folders);

        return new PendingSyncOutcome(ciphers, folders);
    }

    // ───────────────────────── 条目 ─────────────────────────

    private async Task<int> PushCiphersAsync(AccountData account, byte[] encKey, byte[] macKey, DateTime now, CancellationToken ct)
    {
        var candidates = await _dbVault.Ciphers
            .Where(c => c.SyncStatus == "pending" || c.SyncStatus == "failed")
            .OrderBy(c => c.LastAttempt)
            .Take(CandidatePool)
            .ToListAsync(ct);

        // 取 pending **和 failed**：
        //   · pending = 刚写下、还没试过或正在重试
        //   · failed  = 重试超限过（UI 上要显性化），但**不是永久放弃**：以前 failed 被排除在查询
        //     之外，等于把用户的修改永久留在本地推不上去，而且下一次拉取还会用服务器旧值把它覆盖掉。
        var pending = candidates.Where(c => SyncBackoff.IsDue(c, now)).Take(BatchSize).ToList();
        if (pending.Count == 0) return 0;

        Log.Information("Processing {Count} pending ciphers", pending.Count);
        var done = 0;

        foreach (var cipher in pending)
        {
            var op = cipher.PendingOp;
            try
            {
                if (op == "delete")
                {
                    if (await _bitwarden.TrashCipherAsync(account.AccessToken, cipher.Id.ToString()))
                    {
                        _dbVault.Ciphers.Remove(cipher);
                        done++;
                        Log.Information("Synced delete for cipher {Id}", cipher.Id);
                    }
                    else MarkFailed(cipher);
                }
                else if (op == "create")
                {
                    var body = CipherMapper.ToRemoteBody(cipher, encKey, macKey);
                    var result = await _bitwarden.CreateCipherAsync(account.AccessToken,
                        Convert.ToBase64String(encKey), Convert.ToBase64String(macKey), body);
                    if (result != null && Guid.TryParse(result.Id, out var serverId))
                    {
                        // 回写服务器 ID。⚠ 不能直接改主键（EF 不允许改已跟踪实体的键），
                        // 必须删旧行 + 插新键的新行；见 Cipher.DeepCopyWithId 的注释。
                        var oldId = cipher.Id;
                        var remapped = cipher.DeepCopyWithId(serverId);
                        _dbVault.Ciphers.Remove(cipher);
                        _dbVault.Ciphers.Add(remapped);
                        done++;
                        Log.Information("Synced create for cipher {OldId} -> {NewId}", oldId, serverId);
                    }
                    else MarkFailed(cipher);
                }
                else if (op == "update")
                {
                    var body = CipherMapper.ToRemoteBody(cipher, encKey, macKey);
                    if (await _bitwarden.UpdateCipherAsync(account.AccessToken, cipher.Id.ToString(), body))
                    {
                        MarkSynced(cipher);
                        done++;
                        Log.Information("Synced update for cipher {Id}", cipher.Id);
                    }
                    else MarkFailed(cipher);
                }
                else
                {
                    // PendingOp 为空 = 队列元素缺操作字段。条目的每条写路径都会带上它，
                    // 落到这里说明是别处写坏了数据 → 只记日志，不猜（猜错会把服务器上的内容搞乱）。
                    Log.Warning("Cipher {Id} is {Status} but has no PendingOp, skipping", cipher.Id, cipher.SyncStatus);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to sync cipher {Id}", cipher.Id);
                MarkFailed(cipher);
            }
        }

        return done;
    }

    private static void MarkSynced(Cipher cipher)
    {
        cipher.SyncStatus = "synced";
        cipher.PendingOp = null;
        cipher.RetryCount = 0;
        cipher.LastAttempt = null;
    }

    private static void MarkFailed(Cipher cipher)
    {
        cipher.RetryCount++;
        cipher.LastAttempt = DateTime.UtcNow;
        if (cipher.RetryCount > FailedAfterRetries && cipher.SyncStatus != "failed")
        {
            // 只是把状态显性化成 failed 让 UI 能看出来，**不代表放弃**：
            // 查询依然会捞 failed，退避到点还会再试；用户在 UI 上改一次会重置 RetryCount。
            cipher.SyncStatus = "failed";
            Log.Warning("Cipher {Id} marked as failed after {Count} retries (will keep retrying with backoff)",
                cipher.Id, cipher.RetryCount);
        }
    }

    // ───────────────────────── 文件夹 ─────────────────────────

    private async Task<int> PushFoldersAsync(AccountData account, byte[] encKey, byte[] macKey, DateTime now, CancellationToken ct)
    {
        var candidates = await _dbVault.Folders
            .Where(f => f.SyncStatus == "pending" || f.SyncStatus == "failed")
            .OrderBy(f => f.LastAttempt)
            .Take(CandidatePool)
            .ToListAsync(ct);

        var pending = candidates.Where(f => SyncBackoff.IsDue(f, now)).Take(BatchSize).ToList();
        if (pending.Count == 0) return 0;

        Log.Information("Processing {Count} pending folders", pending.Count);
        var done = 0;

        foreach (var folder in pending)
        {
            // PendingOp 为空的历史行：只可能是"改过名但推送失败"——旧的 Rename 分支只把 SyncStatus
            // 改成 pending、没落操作字段。那种行的 Id 一定来自服务器（能改名说明它已存在），按 update 处理。
            var op = string.IsNullOrEmpty(folder.PendingOp) ? "update" : folder.PendingOp;
            try
            {
                if (op == "delete")
                {
                    if (await _bitwarden.DeleteFolderAsync(account.AccessToken, folder.Id.ToString()))
                    {
                        await _folders.RemoveLocallyAsync(account.Id, folder.Id);
                        done++;
                        Log.Information("Synced delete for folder {Id}", folder.Id);
                    }
                    else MarkFailed(folder);
                }
                else if (op == "create")
                {
                    var result = await _bitwarden.CreateFolderAsync(account.AccessToken, folder.Name, encKey, macKey);
                    if (result != null && Guid.TryParse(result.Id, out var serverId))
                    {
                        var localId = folder.Id;
                        _dbVault.Folders.Remove(folder);
                        _dbVault.Folders.Add(folder.DeepCopyWithId(serverId));

                        // 指向它的条目必须跟着换 Id：不换的话它们全变成"指向一个不存在的文件夹"
                        // （界面上显示未分类，而且下次推条目时会把那个本地 Guid 当 folderId 发给服务器）。
                        var referencing = await _dbVault.Ciphers.Where(c => c.FolderId == localId).ToListAsync(ct);
                        foreach (var c in referencing) c.FolderId = serverId;

                        done++;
                        Log.Information("Synced create for folder {OldId} -> {NewId} ({Refs} ciphers repointed)",
                            localId, serverId, referencing.Count);
                    }
                    else MarkFailed(folder);
                }
                else // update
                {
                    if (await _bitwarden.UpdateFolderAsync(account.AccessToken, folder.Id.ToString(), folder.Name, encKey, macKey))
                    {
                        folder.SyncStatus = "synced";
                        folder.PendingOp = null;
                        folder.RetryCount = 0;
                        folder.LastAttempt = null;
                        done++;
                        Log.Information("Synced update for folder {Id}", folder.Id);
                    }
                    else MarkFailed(folder);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to sync folder {Id}", folder.Id);
                MarkFailed(folder);
            }
        }

        return done;
    }

    private static void MarkFailed(Folder folder)
    {
        folder.RetryCount++;
        folder.LastAttempt = DateTime.UtcNow;
        if (folder.RetryCount > FailedAfterRetries && folder.SyncStatus != "failed")
        {
            folder.SyncStatus = "failed";
            Log.Warning("Folder {Id} marked as failed after {Count} retries (will keep retrying with backoff)",
                folder.Id, folder.RetryCount);
        }
    }
}
