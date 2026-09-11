using System.Text.Json;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Auth;
using Tama.Services.Bitwarden;
using Microsoft.EntityFrameworkCore;

using Tama.Core.Exceptions;
using Serilog;

namespace Tama.Services.Vault;

/// <summary>
/// 文件夹：离线优先——本地 Folders 表始终可用（无 Bitwarden 账号时纯本地 CRUD），
/// 有账号时创建/更新/删除加密推送到 Bitwarden，成功才写 SyncCache；
/// 列表 = 本地表 ∪ 同步缓存（按 Id 去重）。
/// 实现 <see cref="IFolderApi"/>。
/// </summary>
public class FolderService : IFolderApi
{
    private readonly AuthDbContext _db;
    private readonly TamaDbContext _dbVault;
    private readonly BitwardenAccountService _accounts;
    private readonly IBitwardenApiClient _bitwarden;
    private static readonly ILogger Log = Serilog.Log.ForContext<FolderService>();

    public FolderService(AuthDbContext db, TamaDbContext dbVault, BitwardenAccountService accounts, IBitwardenApiClient bitwarden)
    {
        _db = db;
        _dbVault = dbVault;
        _accounts = accounts;
        _bitwarden = bitwarden;
    }

    public async Task<List<FolderDto>> List(string? accountId = null)
    {
        // 本地表始终参与（离线也能建文件夹）；有账号时并入 SyncCache 里的远端文件夹
        var result = new List<FolderDto>();
        var seen = new HashSet<Guid>();

        var localFolders = await _dbVault.Folders.AsNoTracking().ToListAsync();
        // 已经删掉、只等推送的那些：本地行要留到推成功才真删（删早了这次删除就丢了），
        // 但**界面上一律当它不存在**，否则用户点了删除、文件夹还在筛选项里杵着。
        var deleting = localFolders.Where(f => f.IsPendingDelete).Select(f => f.Id).ToHashSet();

        foreach (var f in localFolders)
        {
            if (deleting.Contains(f.Id)) continue;
            if (seen.Add(f.Id)) result.Add(new FolderDto(f.Id.ToString(), f.Name, f.SyncStatus));
        }

        var account = !string.IsNullOrEmpty(accountId)
            ? await _db.Accounts.FindAsync(accountId)
            : await _db.Accounts.FirstOrDefaultAsync(a => a.Type == "bitwarden");
        if (account != null)
        {
            var cache = await _db.SyncCaches.AsNoTracking().FirstOrDefaultAsync(c => c.AccountId == account.Id);
            if (cache != null)
            {
                var folders = JsonSerializer.Deserialize<List<BitwardenFolderResponse>>(cache.FoldersJson) ?? new();
                foreach (var f in folders)
                {
                    // 缓存里还留着待删的文件夹（要等推成功才清），这里必须挡住，否则它会从缓存"复活"
                    if (Guid.TryParse(f.Id, out var gid))
                    {
                        if (deleting.Contains(gid)) continue;
                        if (!seen.Add(gid)) continue;
                    }
                    result.Add(new FolderDto(f.Id, f.Name, "synced"));   // 缓存里那份来自服务器快照
                }
            }
        }

        return result;
    }

    public async Task<FolderCreateResponse> Create(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new TamaException("Name required");

        var keys = await _accounts.FindBitwardenAccount(null);
        if (keys != null)
        {
            var (accountId, accessToken, encKey, macKey) = keys.Value;
            try
            {
                var result = await _bitwarden.CreateFolderAsync(accessToken, name, encKey, macKey);
                if (result != null)
                {
                    // 本地一致性：写入本地表 + 更新 SyncCache JSON，创建后立即可见（List 读 SyncCache）
                    if (Guid.TryParse(result.Id, out var newFolderGuid) && !_dbVault.Folders.Any(f => f.Id == newFolderGuid))
                    {
                        _dbVault.Folders.Add(new Folder
                        {
                            Id = newFolderGuid,
                            Name = name,
                            SyncStatus = "synced",
                        });
                        await _dbVault.SaveChangesAsync();
                    }

                    var cache = await _db.SyncCaches.FirstOrDefaultAsync(c => c.AccountId == accountId);
                    if (cache != null)
                    {
                        var folders = JsonSerializer.Deserialize<List<BitwardenFolderResponse>>(cache.FoldersJson) ?? new();
                        folders.RemoveAll(f => f.Id == result.Id);
                        folders.Add(new BitwardenFolderResponse { Id = result.Id, Name = name });
                        cache.FoldersJson = JsonSerializer.Serialize(folders);
                        await _db.SaveChangesAsync();
                    }

                    return new FolderCreateResponse(result.Id, name);
                }
            }
            catch (Exception ex)
            {
                // 断网是最常见的"离线"，而它在这里是**抛异常**（不是返回 null）。
                // 不接住的话：界面报错、本地什么都不落 —— 与条目的行为（catch 住留 pending）不一致，
                // 也违背了"离线也能用"。
                Log.Warning(ex, "Folder create push failed, keeping it local and queued");
            }
        }

        // 离线/无账号/推送失败：纯本地文件夹（pending/create，等后台队列 PendingSyncProcessor 推）。
        // 这种行**必须**被队列消费掉：以前队列只查 Ciphers，于是"离线建的文件夹"永远只存在本机，
        // 界面上一切正常、云端却永远看不到它（连后来才登录 Bitwarden 也补不上去）。
        var localId = Guid.NewGuid();
        _dbVault.Folders.Add(new Folder
        {
            Id = localId,
            Name = name,
            SyncStatus = "pending",
            PendingOp = "create",
        });
        await _dbVault.SaveChangesAsync();
        return new FolderCreateResponse(localId.ToString(), name);
    }

    public async Task Rename(string id, string name)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) throw new TamaException("Id and name required");
        if (!Guid.TryParse(id, out var folderGuid)) throw new TamaException("Invalid folder id");

        var keys = await _accounts.FindBitwardenAccount(null);
        if (keys != null)
        {
            var (accountId, accessToken, encKey, macKey) = keys.Value;
            try
            {
                var result = await _bitwarden.UpdateFolderAsync(accessToken, id, name, encKey, macKey);
                if (result)
                {
                    // 本地一致性：更新本地表 + SyncCache JSON
                    var local = await _dbVault.Folders.FindAsync(folderGuid);
                    if (local != null && local.SyncStatus != "pending")
                    {
                        local.Name = name;
                        local.SyncStatus = "synced";
                        await _dbVault.SaveChangesAsync();
                    }

                    var cache = await _db.SyncCaches.FirstOrDefaultAsync(c => c.AccountId == accountId);
                    if (cache != null)
                    {
                        var folders = JsonSerializer.Deserialize<List<BitwardenFolderResponse>>(cache.FoldersJson) ?? new();
                        var existing = folders.FirstOrDefault(f => f.Id == id);
                        if (existing != null) existing.Name = name;
                        else folders.Add(new BitwardenFolderResponse { Id = id, Name = name });
                        cache.FoldersJson = JsonSerializer.Serialize(folders);
                        await _db.SaveChangesAsync();
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                // 断网是抛异常而不是返回 false —— 接住它，走下面的排队分支（与条目一致）
                Log.Warning(ex, "Folder rename push failed, queuing it");
            }
        }

        // 离线/无账号/推送失败：改本地行 + 落下操作字段，交给后台队列（PendingSyncProcessor）
        var pending = await _dbVault.Folders.FindAsync(folderGuid) ?? throw new TamaException("Folder not found");
        pending.Name = name;
        // ⚠ 只把 SyncStatus 改成 pending 是不够的：队列元素靠 PendingOp 才知道该做什么。
        //   以前这里不落 PendingOp，"改名失败"的行于是永远没人认得出该 update 还是 create。
        //   还没建上去的（create）保持 create——改名只是改这次创建的内容。
        if (pending.PendingOp != "create") pending.PendingOp = "update";
        if (pending.SyncStatus != "pending") pending.SyncStatus = "pending";
        pending.RetryCount = 0;      // 用户重新编辑 = 新的推送意图，重置重试预算（与 Cipher 同口径）
        pending.LastAttempt = null;
        await _dbVault.SaveChangesAsync();
    }

    public async Task Delete(string id)
    {
        if (string.IsNullOrEmpty(id)) throw new TamaException("Id required");
        if (!Guid.TryParse(id, out var folderGuid)) throw new TamaException("Invalid folder id");

        var folder = await _dbVault.Folders.FindAsync(folderGuid)
            ?? throw new TamaException("Folder not found");

        // 从没推上去过的本地文件夹（pending/create）：服务器根本不知道它存在，
        // 硬推一次 delete 只会拿到 404 → 落成"永远重试一个不存在的 ID"。直接删本地。
        if (folder.SyncStatus != "synced" && folder.PendingOp == "create")
        {
            await RemoveLocallyAsync(null, folderGuid);
            return;
        }

        var keys = await _accounts.FindBitwardenAccount(null);
        if (keys != null)
        {
            var (accountId, accessToken, _, _) = keys.Value;
            try
            {
                if (await _bitwarden.DeleteFolderAsync(accessToken, id))
                {
                    await RemoveLocallyAsync(accountId, folderGuid);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Folder delete push failed for {Id}, queuing it", folder.Id);
            }

            // 推送没成功 → 排进队列等后台重试，**不再抛错**：
            // 与条目一致（离线优先 + 乐观写入），用户看到的"删掉了"是真的（列表会滤掉它）。
            folder.SyncStatus = "pending";
            folder.PendingOp = "delete";
            folder.RetryCount = 0;
            folder.LastAttempt = null;
            await _dbVault.SaveChangesAsync();
            return;
        }

        // 纯本地库（没有关联账号）：本来就没有云端，直接删
        await RemoveLocallyAsync(null, folderGuid);
    }

    /// <summary>
    /// 本地把文件夹删干净：删行 + 引用它的条目回到"无文件夹" + 同步缓存里也去掉。
    ///
    /// <c>internal</c> 是因为它是两个调用方共用的：用户点删除（<see cref="Delete"/>），
    /// 以及后台把删除推成功后收尾（<c>PendingSyncProcessor</c>）。缓存那一半**不能省**——
    /// <see cref="List"/> 会把 SyncCache 里的文件夹并进结果，缓存不更新的话删掉的文件夹会"复活"。
    /// </summary>
    internal async Task RemoveLocallyAsync(string? accountId, Guid id)
    {
        var folder = await _dbVault.Folders.FindAsync(id);
        if (folder != null) _dbVault.Folders.Remove(folder);

        // Bitwarden 删除文件夹时引用它的 cipher 回到"无文件夹"（folderId = null）
        var affected = await _dbVault.Ciphers
            .Where(c => c.FolderId == id)
            .ToListAsync();
        foreach (var c in affected) c.FolderId = null;

        await _dbVault.SaveChangesAsync();

        if (accountId == null) return;
        var cache = await _db.SyncCaches.FirstOrDefaultAsync(c => c.AccountId == accountId);
        if (cache != null)
        {
            var folders = JsonSerializer.Deserialize<List<BitwardenFolderResponse>>(cache.FoldersJson) ?? new();
            var updated = folders.Where(f => f.Id != id.ToString()).ToList();
            cache.FoldersJson = JsonSerializer.Serialize(updated);
            await _db.SaveChangesAsync();
        }
    }
}
