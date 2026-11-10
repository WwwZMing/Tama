using System.Text.Json;
using Tama.Core.Contracts;
using Tama.Core.Exceptions;
using Tama.Core.Formats;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Vault;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Tama.Services.Import;

/// <summary>
/// Tama 自家 JSON 备份（<c>tama-json</c>）的还原。实现 <see cref="IVaultImportApi"/>。
///
/// 与 <see cref="KeePassImportService"/> 的分工：那个吃 KDBX、只产 Login；
/// 这个吃自家格式、要还原四种类型 + 标签 + 自定义字段 + 文件夹。
///
/// 落库一律走 <see cref="CipherService.Create"/>，不自己写 DbContext——
/// 这样导入的条目和手工新建的走**同一条**乐观写入链路（本地加密落库 + 有账号则推送 +
/// 失败留 pending 给 SyncWorker 重试）。绕过它自己插库的话，导入的条目永远不会被推上云端。
/// </summary>
public class VaultImportService : IVaultImportApi
{
    private readonly CipherService _cipher;
    private readonly FolderService _folders;
    private readonly TamaDbContext _dbVault;
    private static readonly ILogger Log = Serilog.Log.ForContext<VaultImportService>();

    public VaultImportService(CipherService cipher, FolderService folders, TamaDbContext dbVault)
    {
        _cipher = cipher;
        _folders = folders;
        _dbVault = dbVault;
    }

    // ═══════════════════════ 预览 ═══════════════════════

    /// <summary>只读预览；任何解析失败都收成 <see cref="VaultImportPreview.Error"/>，不抛异常（UI 要能显示原因）。</summary>
    public VaultImportPreview Preview(byte[] file)
    {
        if (file == null || file.Length == 0)
            return VaultImportPreview.NotTamaVault("文件为空");

        if (!TryParse(file, out var root, out var error))
            return VaultImportPreview.NotTamaVault(error!);

        var types = root!.Items
            .Where(i => i != null)
            .GroupBy(i => ResolveType(i))
            .Select(g => new VaultImportTypeCount(TypeLabel(g.Key), g.Count()))
            .OrderBy(t => t.Type, StringComparer.Ordinal)
            .ToList();

        var folders = root.Items
            .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Folder))
            .Select(i => i.Folder!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return new VaultImportPreview(
            IsTamaVault: true,
            Format: root.Format,
            Version: root.Version,
            Error: null,
            Total: root.Items.Count(i => i != null),
            Types: types,
            Folders: folders,
            ExportedAt: root.ExportedAt == default ? null : root.ExportedAt);
    }

    // ═══════════════════════ 导入 ═══════════════════════

    public async Task<VaultImportResponse> Import(byte[] file, VaultImportOptions? options = null)
    {
        options ??= new VaultImportOptions();

        if (file == null || file.Length == 0)
            throw new TamaException("文件为空");

        if (!TryParse(file, out var root, out var error))
            throw new TamaException(error ?? "无法解析备份文件");

        var items = root!.Items.Where(i => i != null).ToList();
        var warnings = new List<string>();

        // === 文件夹：按名字映射到本地 Id，缺的建出来（同名复用，不重复建）===
        var folderMap = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var foldersCreated = 0;

        if (options.RestoreFolders)
        {
            // IsPendingDelete 是 C# 计算属性，翻不成 SQL —— 必须先 ToList 再在内存里滤
            var existing = await _dbVault.Folders.AsNoTracking().ToListAsync();
            foreach (var f in existing.Where(f => !f.IsPendingDelete))
                folderMap.TryAdd(f.Name, f.Id);

            var wanted = items
                .Select(i => i.Folder)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var name in wanted)
            {
                if (folderMap.ContainsKey(name)) continue;
                var created = await _folders.Create(name);
                if (Guid.TryParse(created.Id, out var gid))
                {
                    folderMap[name] = gid;
                    foldersCreated++;
                }
                else
                {
                    warnings.Add($"文件夹「{name}」创建失败，其中条目归入未分类");
                }
            }
        }

        // === 去重集合：同类型 + 同名称 + 同用户名 ===
        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.SkipDuplicates)
        {
            var existing = await _dbVault.Ciphers.AsNoTracking()
                .Where(c => c.DeletedAt == null)
                .Select(c => new { c.Type, c.Name, Username = c.Login != null ? c.Login.Username : null })
                .ToListAsync();
            foreach (var e in existing)
                existingKeys.Add(DuplicateKey(e.Type, e.Name, e.Username));
        }

        var imported = 0;
        var skipped = 0;

        foreach (var item in items)
        {
            var type = ResolveType(item);
            var login = ResolveLogin(item);

            if (options.SkipDuplicates &&
                existingKeys.Contains(DuplicateKey(type, item.Name, login?.Username)))
            {
                skipped++;
                continue;
            }

            Guid? folderId = null;
            if (options.RestoreFolders &&
                !string.IsNullOrWhiteSpace(item.Folder) &&
                folderMap.TryGetValue(item.Folder!, out var fid))
            {
                folderId = fid;
            }

            await _cipher.Create(new CreateCipherRequest
            {
                Type = (int)type,
                Name = item.Name ?? "",
                Notes = item.Notes,
                Favorite = item.Favorite,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
                FolderId = folderId?.ToString(),
                Login = login != null ? new CipherLoginRequest
                {
                    Username = login.Username,
                    Password = login.Password,
                    Totp = login.Totp,
                    Uris = login.Uris,
                } : null,
                Card = item.Card != null ? new CipherCardRequest
                {
                    CardholderName = item.Card.CardholderName,
                    Number = item.Card.Number,
                    Brand = item.Card.Brand,
                    ExpMonth = item.Card.ExpMonth,
                    ExpYear = item.Card.ExpYear,
                    Code = item.Card.Code,
                } : null,
                Identity = item.Identity != null ? new CipherIdentityRequest
                {
                    FirstName = item.Identity.FirstName,
                    LastName = item.Identity.LastName,
                    Email = item.Identity.Email,
                    Phone = item.Identity.Phone,
                    Address = item.Identity.Address,
                    Ssn = item.Identity.Ssn,
                    Username = item.Identity.Username,
                } : null,
                Tags = item.Tags,
                Fields = item.Fields?.Select(f => new CipherFieldRequest
                {
                    Name = f.Name ?? "",
                    Value = f.Value,
                    Type = f.Type,
                    Hidden = f.Hidden,
                }).ToList(),
            });

            // 同一份文件里出现两条完全一样的条目时，后一条也要被自己的前一条挡住
            existingKeys.Add(DuplicateKey(type, item.Name, login?.Username));
            imported++;
        }

        Log.Information("Vault import (tama-json v{Version}): {Imported} imported, {Skipped} skipped, {Folders} folders created",
            root.Version, imported, skipped, foldersCreated);

        return new VaultImportResponse(imported, skipped, foldersCreated, warnings);
    }

    // ═══════════════════════ 解析与映射 ═══════════════════════

    /// <summary>解析并校验是自家备份。失败时给出**能直接给用户看**的原因。</summary>
    private static bool TryParse(byte[] file, out TamaVaultFile? root, out string? error)
    {
        root = null;
        error = null;

        TamaVaultFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TamaVaultFile>(file, TamaVaultJson.ReadOptions);
        }
        catch (JsonException ex)
        {
            error = "不是有效的 JSON 文件：" + ex.Message;
            return false;
        }

        if (parsed == null)
        {
            error = "文件内容为空";
            return false;
        }

        // 格式标记是唯一的判据：用户很容易把别家的导出（Bitwarden/1Password）选到这里来，
        // 那些文件也是合法 JSON，不挡住的话会被当成"0 条"静默导入成功。
        if (!string.Equals(parsed.Format, TamaVaultJson.Format, StringComparison.OrdinalIgnoreCase))
        {
            error = string.IsNullOrWhiteSpace(parsed.Format)
                ? "缺少 format 标记，不是 Tama 备份文件"
                : $"不是 Tama 备份文件（format=\"{parsed.Format}\"）";
            return false;
        }

        if (parsed.Version > TamaVaultJson.CurrentVersion)
        {
            error = $"备份版本 v{parsed.Version} 比当前应用（v{TamaVaultJson.CurrentVersion}）新，请先升级应用";
            return false;
        }

        parsed.Items ??= new();
        root = parsed;
        return true;
    }

    /// <summary>
    /// 定类型：先认 <c>type</c> 字段，认不出就**按带回来的段落反推**。
    /// 反推存在的意义：缺了 type 的老文件里，一张银行卡不能因为没写类型就被塞成登录条目。
    /// </summary>
    private static CipherType ResolveType(TamaVaultItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Type) &&
            Enum.TryParse<CipherType>(item.Type, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        if (item.Card != null) return CipherType.Card;
        if (item.Identity != null) return CipherType.Identity;
        if (item.Login != null || item.Username != null || item.Password != null || item.Totp != null)
            return CipherType.Login;
        return CipherType.SecureNote;
    }

    /// <summary>登录载荷：优先 v2 的嵌套段，回退 v1 的扁平字段。两者都没有则返回 null。</summary>
    private static TamaVaultLogin? ResolveLogin(TamaVaultItem item)
    {
        if (item.Login != null) return item.Login;

        var hasFlat = item.Username != null || item.Password != null ||
                      item.Totp != null || item.Uris is { Count: > 0 };
        if (!hasFlat) return null;

        return new TamaVaultLogin
        {
            Username = item.Username,
            Password = item.Password,
            Totp = item.Totp,
            Uris = item.Uris,
        };
    }

    private static string DuplicateKey(CipherType type, string? name, string? username) =>
        $"{(int)type}\u0001{name?.Trim()}\u0001{username?.Trim()}";

    private static string TypeLabel(CipherType t) => t switch
    {
        CipherType.Login => "登录",
        CipherType.SecureNote => "安全笔记",
        CipherType.Card => "银行卡",
        CipherType.Identity => "身份信息",
        _ => "其它",
    };
}
