using Tama.Core.Contracts;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Auth;
using Tama.Services.Bitwarden;
using Microsoft.EntityFrameworkCore;
using Serilog;

using Tama.Core.Exceptions;

namespace Tama.Services.Vault;

/// <summary>
/// Vault 条目（Cipher）的查询与增删改；写入时加密后推送 Bitwarden，推送失败留 pending 由 SyncWorker 重试。
/// 实现 <see cref="IVaultApi"/>。
/// </summary>
public class CipherService : IVaultApi
{
    private readonly TamaDbContext _dbVault;
    private readonly BitwardenAccountService _accounts;
    private readonly IBitwardenApiClient _bitwarden;
    private static readonly ILogger Log = Serilog.Log.ForContext<CipherService>();

    public CipherService(TamaDbContext dbVault, BitwardenAccountService accounts, IBitwardenApiClient bitwarden)
    {
        _dbVault = dbVault;
        _accounts = accounts;
        _bitwarden = bitwarden;
    }

    public async Task<CipherSearchResponse> Search(CipherSearchRequest? req)
    {
        // 文本查询交给数据库（名称/用户名/URI/备注），分面筛选在内存里做。
        // 为什么不用 N 条 SQL：分面计数要算"其它栏目都生效、但不含自己这一栏"的多组口径，
        // 一栏一组就是四五条查询；本地库就几百条，一次取出来算清楚更省也更不容易错。
        var query = _dbVault.Ciphers.AsNoTracking()
            .Where(c => c.DeletedAt == null);

        if (!string.IsNullOrEmpty(req?.Query))
        {
            var q = req.Query.ToLower();
            query = query.Where(c =>
                c.Name.ToLower().Contains(q) ||
                (c.Login != null && c.Login.Username != null && c.Login.Username.ToLower().Contains(q)) ||
                (c.Login != null && c.Login.Uris != null && c.Login.Uris.Any(u => u.ToLower().Contains(q))) ||
                (c.Notes != null && c.Notes.ToLower().Contains(q))
            );
        }

        var baseSet = await query.ToListAsync();
        // 已删待推送的文件夹一律当它不存在（它是"马上就不在了"的状态）：
        // 放进筛选项里会让用户以为刚才那次删除没生效；引用它的条目同时会落进"未分类"，
        // 与 CipherUi.FolderName / CipherService.IsUnfiled 对悬空 Id 的口径一致。
        // ⚠ 不能写 f.IsPendingDelete —— 那是 C# 计算属性，EF 翻不成 SQL。
        var folders = await _dbVault.Folders.AsNoTracking()
            .Where(f => f.SyncStatus != "pending" || f.PendingOp != "delete")
            .ToListAsync();

        var types = req?.Types is { Count: > 0 } ? req.Types : null;
        var folderIds = req?.FolderIds is { Count: > 0 } ? req.FolderIds : null;
        var hasTotp = req?.HasTotp == true;
        var hasPasskey = req?.HasPasskey == true;

        // "未分类" 不只等于 FolderId == null：Bitwarden 的组织条目 folderId 可能指向 **collection**，
        // 而 sync 响应的 folders 只有个人文件夹 → 本地会出现"指向未知文件夹"的悬空 Id。
        // UI 那边（CipherUi.FolderName）早就把这种显示成"未分类"了，计数必须跟它一致，
        // 否则筛选栏各项相加 ≠ 总数（实测差 1：组织条目的集合 Id 本地不存在）。
        var knownFolderIds = folders.Select(f => f.Id).ToHashSet();
        bool IsUnfiled(Cipher c) => c.FolderId == null || !knownFolderIds.Contains(c.FolderId.Value);

        bool ByFolder(Cipher c) => folderIds == null
            || (IsUnfiled(c) ? folderIds.Contains("") : folderIds.Contains(c.FolderId!.Value.ToString()));
        bool ByType(Cipher c) => types == null || types.Contains((int)c.Type);
        bool ByTotp(Cipher c) => !hasTotp || !string.IsNullOrEmpty(c.Login?.Totp);
        bool ByPasskey(Cipher c) => !hasPasskey || HasPasskey(c);

        var ciphers = baseSet.Where(c => ByFolder(c) && ByType(c) && ByTotp(c) && ByPasskey(c)).ToList();

        // 分面：算某一栏时把它们自己那一栏的谓词摘掉（见 CipherFacets 的注释）
        var typeFacets = FacetTypes
            .Select(t => new TypeFacet(t, baseSet.Count(c => (int)c.Type == t && ByFolder(c) && ByTotp(c) && ByPasskey(c))))
            .ToList();

        var folderFacets = new List<FolderFacet>
        {
            new("", "未分类", baseSet.Count(c => IsUnfiled(c) && ByType(c) && ByTotp(c) && ByPasskey(c))),
        };
        folderFacets.AddRange(folders.Select(f => new FolderFacet(
            f.Id.ToString(),
            f.Name,
            baseSet.Count(c => c.FolderId == f.Id && ByType(c) && ByTotp(c) && ByPasskey(c)))));

        var facets = new CipherFacets(
            Total: baseSet.Count,
            Types: typeFacets,
            Folders: folderFacets,
            // 「属性」栏两个开关的计数：各自排除**自己**那个谓词、保留另一个。
            // 写成 ByTotp/ByPasskey 的交叉（而不是两个都留或都去）才是"再选它还能不能查到"。
            TotpCount: baseSet.Count(c => !string.IsNullOrEmpty(c.Login?.Totp) && ByFolder(c) && ByType(c) && ByPasskey(c)),
            PasskeyCount: baseSet.Count(c => HasPasskey(c) && ByFolder(c) && ByType(c) && ByTotp(c)));

        Log.Information("CipherSearch: {Count}/{Total} ciphers, {Folders} folders (q={Q}, types={T}, folders={F}, totp={Totp}, passkey={Pk})",
            ciphers.Count, baseSet.Count, folders.Count, req?.Query,
            types == null ? "*" : string.Join(",", types),
            folderIds == null ? "*" : string.Join(",", folderIds),
            hasTotp, hasPasskey);

        return new CipherSearchResponse(
            ciphers.Select(ToDto).ToList(),
            folders.Select(f => new FolderDto(f.Id.ToString(), f.Name, f.SyncStatus)).ToList(),
            facets);
    }

    /// <summary>分面里的类型枚举：固定四种（0 条的也要列出来，UI 负责灰掉）</summary>
    private static readonly int[] FacetTypes = { 1, 2, 3, 4 };

    /// <summary>「带通行密钥」的判据与 PasskeyService 的合并列表保持一致（Fido2 凭据非空）</summary>
    private static bool HasPasskey(Cipher c) => c.Fido2Credentials is { Count: > 0 };

    public async Task<CipherDto?> Get(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return null;
        var c = await _dbVault.Ciphers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == guid && x.DeletedAt == null);
        return c == null ? null : ToDto(c);
    }

    public async Task<CipherCreateResponse> Create(CreateCipherRequest req)
    {
        if (req == null) throw new TamaException("Invalid request");

        // 乐观写入：先落本地 pending，推送失败时由 SyncWorker 后台重试（offline-first）
        var localId = Guid.NewGuid();
        var localCipher = new Cipher
        {
            Id = localId,
            Type = (CipherType)req.Type,
            Name = req.Name ?? "",
            Notes = req.Notes,
            Favorite = false,
            SyncStatus = "pending",
            PendingOp = "create",
            // 新建时也能直接放进文件夹。以前契约里没有这个字段、这里硬编码 null，
            // 而编辑器表单照样显示「文件夹」下拉 → 用户选了不生效（静默丢掉选择）。
            FolderId = Guid.TryParse(req.FolderId, out var newFolderId) ? newFolderId : null,
            Login = req.Login != null ? new CipherLogin
            {
                Username = req.Login.Username,
                Password = req.Login.Password,
                Uris = req.Login.Uris ?? new(),
                Totp = req.Login.Totp,
            } : null,
        };
        _dbVault.Ciphers.Add(localCipher);
        await _dbVault.SaveChangesAsync();

        var keys = await _accounts.FindBitwardenAccount(req.AccountId);
        if (keys != null)
        {
            var (_, accessToken, encKey, macKey) = keys.Value;
            var body = CipherMapper.ToRemoteBody(localCipher, encKey, macKey);
            try
            {
                var result = await _bitwarden.CreateCipherAsync(accessToken, Convert.ToBase64String(encKey), Convert.ToBase64String(macKey), body);
                if (result != null)
                {
                    // 推送成功：把本地行换成服务器 ID
                    // ⚠ 不能直接 `localCipher.Id = ...`：EF 不允许修改已跟踪实体的键属性，
                    //   那样会在下一次 SaveChanges 上抛异常、被下面的 catch 吞掉 → 本地永远停在 pending
                    //   → worker 再推一次 create → **云端出现重复条目**（实测就是这么发现的）。
                    //   正确做法：删旧行 + 以新键插入一条内容完整的新行，同一个 SaveChanges 里完成。
                    var remapped = localCipher.DeepCopyWithId(Guid.Parse(result.Id));
                    _dbVault.Ciphers.Remove(localCipher);
                    _dbVault.Ciphers.Add(remapped);
                    await _dbVault.SaveChangesAsync();
                    Log.Information("Cipher create pushed: local {LocalId} -> server {ServerId}", localId, result.Id);
                    return new CipherCreateResponse(result.Id, req.Name, "synced");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cipher create push failed, keeping pending");
            }
        }

        // 离线或推送失败：保留本地 pending 记录，由 SyncWorker 重试
        return new CipherCreateResponse(localId.ToString(), req.Name, "pending");
    }

    public async Task Update(UpdateCipherRequest req)
    {
        if (req == null || string.IsNullOrEmpty(req.Id)) throw new TamaException("Invalid request");

        // 顺序要紧：**先落本地，再拿本地实体构造请求体**。
        // 反过来的话请求体只能从 UI 传来的 DTO 构造，而 DTO 里没有卡片/身份/自定义字段——
        // 一张拉下来的卡片只要被"收藏"一下，就会被推成"卡片内容是空"的条目（服务器上的数据被清掉）。
        var local = await ApplyLocalUpdateAsync(req.Id, req, "pending");

        var keys = await _accounts.FindBitwardenAccount(req.AccountId);
        if (keys == null) return;   // 纯本地库：保持 pending，等关联账号后由 SyncWorker 推

        var (_, accessToken, encKey, macKey) = keys.Value;
        var body = CipherMapper.ToRemoteBody(local, encKey, macKey);
        try
        {
            if (await _bitwarden.UpdateCipherAsync(accessToken, req.Id, body))
            {
                MarkSynced(local);
                await _dbVault.SaveChangesAsync();   // ← 必须落库：只改内存状态等于本地永远显示 pending
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cipher update push failed, keeping pending");
        }
    }

    /// <summary>只改收藏：单独一条路径，避免 UI 为了点个星星去拼"整条覆盖"的请求。</summary>
    public async Task SetFavorite(string id, bool favorite)
    {
        if (string.IsNullOrEmpty(id)) throw new TamaException("Invalid request");

        var local = await GetTrackedOrThrow(id);
        if (local.Favorite == favorite)
        {
            // 幂等：状态没变就别推了，也别把已同步的条目改成 pending
            return;
        }

        local.Favorite = favorite;
        local.UpdatedAt = DateTime.UtcNow;
        local.SyncStatus = "pending";
        local.PendingOp = "update";
        local.RetryCount = 0;
        local.LastAttempt = null;
        await _dbVault.SaveChangesAsync();

        var keys = await _accounts.FindBitwardenAccount(null);
        if (keys == null) return;

        var (_, accessToken, encKey, macKey) = keys.Value;
        var body = CipherMapper.ToRemoteBody(local, encKey, macKey);
        try
        {
            if (await _bitwarden.UpdateCipherAsync(accessToken, local.Id.ToString(), body))
            {
                MarkSynced(local);
                await _dbVault.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Favorite push failed, keeping pending");
        }
    }

    private async Task<Cipher> GetTrackedOrThrow(string id)
    {
        var local = await _dbVault.Ciphers.FindAsync(Guid.Parse(id));
        if (local == null)
            throw new TamaException("条目已不存在（可能刚被后台同步重新映射了 ID），请刷新后重试");
        return local;
    }

    private void MarkSynced(Cipher c)
    {
        c.SyncStatus = "synced";
        c.PendingOp = null;
        c.RetryCount = 0;
        c.LastAttempt = null;
    }

    public async Task Delete(string id, bool softDelete = false)
    {
        if (string.IsNullOrEmpty(id)) throw new TamaException("Invalid request");

        // 离线新建、还没推上去的条目（PendingOp == "create"）：服务器从没见过它，
        // 所以"删除"只需要删本地。硬推一次 delete 只会拿到 404，然后落成 pending delete
        // 让 worker 永远重试一个不存在的 ID——既没意义又会往服务器写一堆无用的失败日志。
        // 找不到条目直接报错：静默"删成功"是骗人的（Update / SetFavorite 同款口径）。
        var localForDelete = await GetTrackedOrThrow(id);
        if (localForDelete.PendingOp == "create")
        {
            _dbVault.Ciphers.Remove(localForDelete);
            await _dbVault.SaveChangesAsync();
            Log.Information("Deleted never-pushed local cipher {Id} without contacting the server", id);
            return;
        }

        var keys = await _accounts.FindBitwardenAccount(null);
        if (keys != null)
        {
            var (_, accessToken, encKey, macKey) = keys.Value;
            try
            {
                var result = softDelete
                    ? await _bitwarden.TrashCipherAsync(accessToken, id)
                    : await _bitwarden.DeleteCipherAsync(accessToken, id);
                if (result)
                {
                    var localId = Guid.Parse(id);
                    var localCipher = await _dbVault.Ciphers.FindAsync(localId);
                    if (localCipher != null)
                    {
                        if (softDelete)
                        {
                            localCipher.DeletedAt = DateTime.UtcNow;
                            localCipher.SyncStatus = "synced";
                            localCipher.PendingOp = null;
                        }
                        else
                        {
                            _dbVault.Ciphers.Remove(localCipher);
                        }
                        await _dbVault.SaveChangesAsync();
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cipher delete push failed, keeping pending");
            }
        }

        // 离线或推送失败：本地软删 + pending delete，由 SyncWorker 重试（回收站语义）
        var lid = Guid.Parse(id);
        var local = await _dbVault.Ciphers.FindAsync(lid);
        if (local != null)
        {
            local.DeletedAt = DateTime.UtcNow;
            local.SyncStatus = "pending";
            local.PendingOp = "delete";
            await _dbVault.SaveChangesAsync();
        }
    }

    private async Task<Cipher> ApplyLocalUpdateAsync(string id, UpdateCipherRequest req, string syncStatus)
    {
        var localCipher = await GetTrackedOrThrow(id);

        localCipher.Name = req.Name ?? "";
        localCipher.Notes = req.Notes;
        localCipher.Favorite = req.Favorite;
        localCipher.FolderId = req.FolderId != null ? Guid.Parse(req.FolderId) : null;
        localCipher.Type = (CipherType)req.Type;
        localCipher.UpdatedAt = DateTime.UtcNow;
        localCipher.SyncStatus = syncStatus;
        localCipher.PendingOp = syncStatus == "pending" ? "update" : null;
        // 用户重新编辑 = 新的推送意图：重置重试预算。
        // 不重置的话，之前已经重试到 failed 的条目会"一试就再次 failed"（RetryCount 只增不减），
        // 实质上永久卡在推不上去的状态里。
        localCipher.RetryCount = 0;
        localCipher.LastAttempt = null;
        if (req.Login != null)
        {
            localCipher.Login ??= new CipherLogin();
            localCipher.Login.Username = req.Login.Username ?? localCipher.Login.Username;
            localCipher.Login.Password = req.Login.Password ?? localCipher.Login.Password;
            localCipher.Login.Totp = req.Login.Totp ?? localCipher.Login.Totp;
            localCipher.Login.Uris = MergeUriLists(localCipher.Login.Uris, req.Login.Uris);
        }
        await _dbVault.SaveChangesAsync();
        return localCipher;
    }

    // === DTO 映射 ===

    public static CipherDto ToDto(Cipher c) => new(
        Id: c.Id.ToString(),
        Type: (int)c.Type,
        Name: c.Name,
        Notes: c.Notes,
        Favorite: c.Favorite,
        FolderId: c.FolderId?.ToString(),
        Login: c.Login != null ? new CipherLoginDto(c.Login.Username, c.Login.Password, c.Login.Uris, c.Login.Totp) : null,
        CreatedDate: c.CreatedAt,
        RevisionDate: c.UpdatedAt,
        SyncStatus: c.SyncStatus);

    // === 导出 ===

    /// <summary>
    /// 导出全部条目为明文 JSON，写入 %USERPROFILE%\Downloads（无下载目录时回退用户目录）。
    /// 文件名含时间戳；返回落盘路径与条目数。内容包含明文密码——调用方（UI）须提示风险。
    /// </summary>
    public Task<VaultExportResponse> Export()
    {
        var ciphers = _dbVault.Ciphers.AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .OrderBy(c => c.Name)
            .ToList();
        // 已删待推送的文件夹不导出（导出是"给你看的现状"，不该带上一个界面上没有的文件夹）
        var folders = _dbVault.Folders.AsNoTracking()
            .Where(f => f.SyncStatus != "pending" || f.PendingOp != "delete")
            .ToList();
        var folderNames = folders.ToDictionary(f => f.Id.ToString(), f => f.Name);

        var items = ciphers.Select(c => new
        {
            name = c.Name,
            type = c.Type.ToString(),
            folder = c.FolderId is { } fid && folderNames.TryGetValue(fid.ToString(), out var fname) ? fname : null,
            favorite = c.Favorite,
            username = c.Login?.Username,
            password = c.Login?.Password,
            totp = c.Login?.Totp,
            uris = c.Login?.Uris,
            notes = c.Notes,
            createdAt = c.CreatedAt,
            updatedAt = c.UpdatedAt,
        });

        var payload = JsonSerializer.Serialize(new
        {
            format = "tama-json",
            version = 1,
            exportedAt = DateTime.UtcNow,
            count = items.Count(),
            items,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fileName = $"tama-export-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        var fullPath = Path.Combine(dir, fileName);
        File.WriteAllText(fullPath, payload);

        Log.Information("Vault exported {Count} items to {Path}", ciphers.Count, fullPath);
        return Task.FromResult(new VaultExportResponse(fullPath, ciphers.Count));
    }

    // === Bitwarden 加密请求构造 ===

    // 请求体构造已统一到 CipherMapper.ToRemoteBody（由本地实体出发，四种类型都带）。
    // 之前这里有**两份**几乎一样的实现（CipherBodySpec 版 + BuildCipherBodyStatic 版），
    // 而且都只发 Login —— 上传一张卡片，服务器收到的是一个"类型是卡片、卡片内容是空"的条目。

    private static List<string> MergeUriLists(List<string>? existing, List<string>? incoming)
    {
        var merged = new List<string>(existing ?? new());
        if (incoming != null)
        {
            foreach (var uri in incoming)
            {
                if (!string.IsNullOrWhiteSpace(uri) &&
                    !merged.Any(m => string.Equals(m, uri, StringComparison.OrdinalIgnoreCase)))
                    merged.Add(uri);
            }
        }
        return merged;
    }
}
