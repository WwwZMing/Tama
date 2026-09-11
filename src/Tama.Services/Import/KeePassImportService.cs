using Tama.Core.Contracts;
using Tama.Core.Models;
using Tama.Core.Exceptions;
using Tama.Services.Vault;

namespace Tama.Services.Import;

/// <summary>
/// KeePass (.kdbx) 导入。实现 <see cref="IImportApi"/>。
/// </summary>
public class KeePassImportService : IImportApi
{
    private readonly CipherService _cipher;

    public KeePassImportService(CipherService cipher) => _cipher = cipher;

    /// <summary>
    /// 解析 KDBX 并返回顶层分组摘要（名称 + 条目数，轻量，供导入时选择"导入哪个文件夹"；全量导入不预览条目防塞爆）。
    /// </summary>
    public List<KdbxGroupDto> ParseGroups(byte[] file, string? password = null)
        => KdbxParser.ParseGroups(file, password);

    /// <summary>
    /// 解析并导入条目（可只导入指定顶层分组，null 为全部），返回导入条数。
    /// 逐条复用 CipherService 的乐观写入链路（本地加密落库 + 有账号则推送，失败留 pending 重试）。
    /// </summary>
    public async Task<int> Import(byte[] file, string? password = null, string? group = null)
    {
        if (file == null || file.Length == 0)
            throw new TamaException("file required");

        var ciphers = Parse(file, password, group);

        var imported = 0;
        foreach (var c in ciphers)
        {
            await _cipher.Create(new CreateCipherRequest
            {
                Type = (int)c.Type,
                Name = c.Name,
                Notes = c.Notes,
                Login = c.Login != null ? new CipherLoginRequest
                {
                    Username = c.Login.Username,
                    Password = c.Login.Password,
                    Uris = c.Login.Uris,
                    Totp = c.Login.Totp,
                } : null,
            });
            imported++;
        }
        return imported;
    }

    /// <summary>
    /// 解析 KDBX 并映射为 Cipher 模型。可只导入指定顶层分组（<paramref name="groupName"/> 为空或 null 时导入全部）。
    /// </summary>
    private static List<Cipher> Parse(byte[] fileBytes, string? password = null, string? groupName = null)
    {
        var ciphers = new List<Cipher>();

        var entries = KdbxParser.Parse(fileBytes, password);

        foreach (var e in entries)
        {
            // 按顶层分组过滤：指定了分组且条目不属于该分组（含根级无分组条目）则跳过
            if (!string.IsNullOrEmpty(groupName))
            {
                var topGroup = e.GroupPath?.Split('/')[0];
                if (!string.Equals(topGroup, groupName, StringComparison.Ordinal))
                    continue;
            }

            var cipher = new Cipher
            {
                Id = Guid.NewGuid(),
                Name = e.Title ?? "",
                Notes = e.Notes,
                Type = CipherType.Login,
                CreatedAt = e.CreationTime,
                UpdatedAt = e.LastModificationTime,
                Favorite = false,
                Tags = new(),
            };

            cipher.Login = new CipherLogin
            {
                Username = e.UserName ?? "",
                Password = e.Password ?? "",
                Totp = e.TOTP,
                Uris = string.IsNullOrEmpty(e.URL) ? new() : new() { e.URL },
            };

            if (e.CustomFields.Count > 0)
            {
                cipher.Fields = e.CustomFields.Select(kv => new CipherField
                {
                    Name = kv.Key,
                    Value = kv.Value,
                    Type = 0,
                    Hidden = false,
                }).ToList();
            }

            ciphers.Add(cipher);
        }

        return ciphers;
    }
}
