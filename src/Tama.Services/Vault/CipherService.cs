using Tama.Core.Contracts;
using Tama.Core.Formats;
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
        var now = DateTime.UtcNow;
        var localCipher = new Cipher
        {
            Id = localId,
            Type = (CipherType)req.Type,
            Name = req.Name ?? "",
            Notes = req.Notes,
            // 以前硬编码 false / 用模型默认的"此刻"——UI 新建时确实该这样，
            // 但从备份还原时会把收藏状态和原始时间抹掉。契约补了可空字段后两者都能表达。
            Favorite = req.Favorite,
            CreatedAt = req.CreatedAt ?? now,
            UpdatedAt = req.UpdatedAt ?? req.CreatedAt ?? now,
            SyncStatus = "pending",
            PendingOp = "create",
            // 新建时也能直接放进文件夹。以前契约里没有这个字段、这里硬编码 null，
            // 而编辑器表单照样显示「文件夹」下拉 → 用户选了不生效（静默丢掉选择）。
            FolderId = Guid.TryParse(req.FolderId, out var newFolderId) ? newFolderId : null,
            Tags = req.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new(),
            Login = req.Login != null ? new CipherLogin
            {
                Username = req.Login.Username,
                Password = req.Login.Password,
                Uris = req.Login.Uris ?? new(),
                Totp = req.Login.Totp,
            } : null,
            // 卡片/身份以前根本没有入口，从备份导回来会变成"类型对、内容空"的条目——
            // 与 CipherMapper 注释里记的老毛病同一类（推一次就把服务器上的内容清掉）。
            Card = req.Card != null ? new CipherCard
            {
                CardholderName = req.Card.CardholderName,
                Number = req.Card.Number,
                Brand = req.Card.Brand,
                ExpMonth = req.Card.ExpMonth,
                ExpYear = req.Card.ExpYear,
                Code = req.Card.Code,
            } : null,
            Identity = req.Identity != null ? new CipherIdentity
            {
                FirstName = req.Identity.FirstName,
                LastName = req.Identity.LastName,
                Email = req.Identity.Email,
                Phone = req.Identity.Phone,
                Address = req.Identity.Address,
                Ssn = req.Identity.Ssn,
                Username = req.Identity.Username,
            } : null,
            // 安全笔记的正文在 Notes 里（见 CipherMapper.ApplyRemote 的注释），
            // 这里只按类型放一个占位，与本地下行映射的口径保持一致。
            SecureNote = (CipherType)req.Type == CipherType.SecureNote ? new CipherNote() : null,
            // Id 是复合主键 (CipherId, Id) 的后半截，必须由写入方按 0,1,2… 编号，
            // 不能指望 SQLite 生成（见 CipherField.Id 的注释）。
            Fields = req.Fields?.Select((f, i) => new CipherField
            {
                Id = i,
                Name = f.Name,
                Value = f.Value,
                Type = f.Type,
                Hidden = f.Hidden,
            }).ToList(),
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
    /// 导出全部条目为明文 JSON，写入用户目录下的 Downloads（无则回退用户目录）。
    /// 文件名含时间戳；返回落盘路径与条目数。内容包含明文密码——调用方（UI）须提示风险。
    ///
    /// v2 起四种类型都写全（v1 只写 Login，银行卡/身份信息/标签/自定义字段**静默丢失**）。
    /// 结构与序列化设置见 <see cref="TamaVaultJson"/>，与导入端共用同一份定义。
    ///
    /// **刻意不导出通行密钥私钥**：那是一条独立的导入/导出通道（通行密钥页），
    /// 把私钥再抄进这份明文备份只会让泄露面变大，不会让还原更完整。
    /// </summary>
    public Task<VaultExportResponse> Export()
    {
        var file = BuildExportFile();
        var payload = JsonSerializer.Serialize(file, TamaVaultJson.WriteOptions);

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fileName = $"tama-export-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        var fullPath = Path.Combine(dir, fileName);
        File.WriteAllText(fullPath, payload);

        Log.Information("Vault exported {Count} items to {Path}", file.Count, fullPath);
        return Task.FromResult(new VaultExportResponse(fullPath, file.Count));
    }

    /// <summary>
    /// 构建导出内容（纯函数，**不落盘**）。抽出来是为了能被单测直接驱动——
    /// 测「导出 → 导入」往返不能真往用户的 ~/Downloads 里写文件。
    /// </summary>
    public TamaVaultFile BuildExportFile()
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

        var items = ciphers.Select(c => new TamaVaultItem
        {
            Name = c.Name,
            Type = c.Type.ToString(),
            Folder = c.FolderId is { } fid && folderNames.TryGetValue(fid.ToString(), out var fname) ? fname : null,
            Favorite = c.Favorite,
            Notes = c.Notes,
            Login = c.Login != null ? new TamaVaultLogin
            {
                Username = c.Login.Username,
                Password = c.Login.Password,
                Totp = c.Login.Totp,
                Uris = c.Login.Uris is { Count: > 0 } ? new List<string>(c.Login.Uris) : null,
            } : null,
            Card = c.Card != null ? new TamaVaultCard
            {
                CardholderName = c.Card.CardholderName,
                Number = c.Card.Number,
                Brand = c.Card.Brand,
                ExpMonth = c.Card.ExpMonth,
                ExpYear = c.Card.ExpYear,
                Code = c.Card.Code,
            } : null,
            Identity = c.Identity != null ? new TamaVaultIdentity
            {
                FirstName = c.Identity.FirstName,
                LastName = c.Identity.LastName,
                Email = c.Identity.Email,
                Phone = c.Identity.Phone,
                Address = c.Identity.Address,
                Ssn = c.Identity.Ssn,
                Username = c.Identity.Username,
            } : null,
            Tags = c.Tags is { Count: > 0 } ? new List<string>(c.Tags) : null,
            Fields = c.Fields is { Count: > 0 } ? c.Fields.Select(f => new TamaVaultField
            {
                Name = f.Name,
                Value = f.Value,
                Type = f.Type,
                Hidden = f.Hidden,
            }).ToList() : null,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
        }).ToList();

        return new TamaVaultFile
        {
            Format = TamaVaultJson.Format,
            Version = TamaVaultJson.CurrentVersion,
            ExportedAt = DateTime.UtcNow,
            Count = items.Count,
            Items = items,
        };
    }

    // === 导出：Bitwarden 格式（给官网「导入数据」用）===

    /// <summary>
    /// 导出为 Bitwarden 的**未加密个人保险库 JSON**，写到下载目录。
    ///
    /// 与 <see cref="Export"/>（tama-json）的区别：那个是 Tama 自己的备份、能无损导回；
    /// 这个是**单向投递**给 Bitwarden 的格式——Bitwarden 侧装不下的东西（通行密钥、标签、
    /// 地址多段拆分）会丢，那是格式本身的限制。
    /// </summary>
    public Task<VaultExportResponse> ExportBitwarden()
    {
        var file = BuildBitwardenExport();
        var payload = JsonSerializer.Serialize(file, BitwardenVaultJson.WriteOptions);

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fileName = $"tama-to-bitwarden-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        var fullPath = Path.Combine(dir, fileName);
        File.WriteAllText(fullPath, payload);

        Log.Information("Vault exported for Bitwarden: {Count} items, {Folders} folders to {Path}",
            file.Items.Count, file.Folders.Count, fullPath);
        return Task.FromResult(new VaultExportResponse(fullPath, file.Items.Count));
    }

    /// <summary>
    /// 构建 Bitwarden 格式的导出内容（纯函数，**不落盘**）——单测据此驱动，
    /// 不必真往用户的 ~/Downloads 里写文件。格式细节与踩坑记录见 <see cref="BitwardenVaultFile"/>。
    /// </summary>
    public BitwardenVaultFile BuildBitwardenExport()
    {
        var ciphers = _dbVault.Ciphers.AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .OrderBy(c => c.Name)
            .ToList();
        var folders = _dbVault.Folders.AsNoTracking()
            .Where(f => f.SyncStatus != "pending" || f.PendingOp != "delete")
            .ToList();

        // 文件夹 id 直接复用本地 Guid：Bitwarden 的导入器只把它当"条目 ↔ 文件夹"的连接键
        // （groupingsMap.set(f.id, ...) 然后 groupingsMap.has(c.folderId)），不要求是它那套 UUID。
        var exportedFolderIds = folders.Select(f => f.Id).ToHashSet();

        var items = ciphers.Select(c => new BitwardenItemJson
        {
            Id = c.Id.ToString(),
            OrganizationId = null,
            // 悬空引用（指向一个不导出的文件夹）宁可不写：写了也匹配不上，
            // 只会让文件里留一个指向不存在文件夹的引用。
            FolderId = c.FolderId is { } fid && exportedFolderIds.Contains(fid) ? fid.ToString() : null,
            Type = (int)c.Type,
            Reprompt = 0,
            Name = c.Name,
            Notes = c.Notes,
            Favorite = c.Favorite,
            Fields = c.Fields is { Count: > 0 } ? c.Fields.Select(f => new BitwardenFieldJson
            {
                Name = f.Name,
                Value = f.Value,
                Type = BitwardenVaultJson.FieldType(f.Type, f.Hidden),
                LinkedId = null,
            }).ToList() : null,

            // 段落按**类型**给，不按"本地有没有这段数据"给 —— Bitwarden 的 toView 也是按 type switch 的，
            // 而且一条空登录也必须带上 login 段，否则导入进去会是"类型是登录、内容是空"。
            Login = c.Type == CipherType.Login ? new BitwardenLoginJson
            {
                // 恒给数组（Bitwarden 自己的导出也是 []）—— 它的 LoginView.uris 默认就是空数组
                Uris = (c.Login?.Uris ?? new()).Select(u => new BitwardenUriJson { Uri = u, Match = null }).ToList(),
                Username = c.Login?.Username,
                Password = c.Login?.Password,
                Totp = c.Login?.Totp,
            } : null,
            SecureNote = c.Type == CipherType.SecureNote ? new BitwardenSecureNoteJson { Type = 0 } : null,
            Card = c.Type == CipherType.Card ? new BitwardenCardJson
            {
                CardholderName = c.Card?.CardholderName,
                Brand = c.Card?.Brand,
                Number = c.Card?.Number,
                ExpMonth = c.Card?.ExpMonth,
                ExpYear = c.Card?.ExpYear,
                Code = c.Card?.Code,
            } : null,
            Identity = c.Type == CipherType.Identity ? new BitwardenIdentityJson
            {
                FirstName = c.Identity?.FirstName,
                LastName = c.Identity?.LastName,
                // 本地只有一个 Address，整段塞进 address1，不猜拆分位置
                Address1 = c.Identity?.Address,
                Email = c.Identity?.Email,
                Phone = c.Identity?.Phone,
                Ssn = c.Identity?.Ssn,
                Username = c.Identity?.Username,
            } : null,
            CreationDate = AsUtc(c.CreatedAt),
            RevisionDate = AsUtc(c.UpdatedAt),
        }).ToList();

        return new BitwardenVaultFile
        {
            Encrypted = false,
            Folders = folders.Select(f => new BitwardenFolderJson { Id = f.Id.ToString(), Name = f.Name }).ToList(),
            Items = items,
        };
    }

    /// <summary>
    /// 打上 UTC 标记再交给 JSON 序列化器。
    ///
    /// 为什么必须做：EF 的 SQLite provider 把 DateTime 存成 TEXT，**读回来是 Kind=Unspecified**，
    /// 于是 System.Text.Json 输出成 <c>"2026-01-01T00:00:00"</c>（不带 Z）。Bitwarden 那边是
    /// <c>new Date(req.creationDate)</c> —— 不带时区的 ISO 串会被当**本地时间**解析，
    /// 东八区下导入后的创建/修改时间整整偏 8 小时。带上 Z 就没有歧义。
    /// </summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

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
