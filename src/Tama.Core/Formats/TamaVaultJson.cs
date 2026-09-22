using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tama.Core.Formats;

/// <summary>
/// Tama 自家保险库备份文件（<c>tama-json</c>）的**唯一一份**结构定义。
///
/// 为什么放在 Core：导出（CipherService.Export）和导入（VaultImportService）必须共用同一份形状。
/// 以前导出用的是方法内的匿名对象——想写导入就得把那份匿名形状再手抄一遍，
/// 两处一旦走样就是"导出能读、导入读不出"或者更糟的"字段静默丢失"。
///
/// 版本历史：
///   · v1（2026-09 之前）：只有扁平的 <c>username/password/totp/uris</c>，
///     银行卡 / 身份信息 / 标签 / 自定义字段**全部丢失**。
///   · v2（本版）：段落改为嵌套 <c>login/card/identity</c>，补齐标签与自定义字段。
///     扁平字段保留在模型上**只为能读回老文件**，导出时一律为 null、不写进 JSON。
/// </summary>
public sealed class TamaVaultFile
{
    /// <summary>
    /// 格式标记。**刻意不给默认值**：给了的话，一份没有 <c>format</c> 字段的别家导出
    /// （Bitwarden/1Password 的 JSON）反序列化后会带着默认值冒充自家备份，
    /// 校验形同虚设 —— 实测就是"成功导入 0 条"，用户以为恢复了，其实一个字都没进来。
    /// </summary>
    public string? Format { get; set; }

    /// <summary>写出版本。同 <see cref="Format"/>，不给默认值（缺字段时保持 0 = 未知）。</summary>
    public int Version { get; set; }

    public DateTime ExportedAt { get; set; }
    public int Count { get; set; }
    public List<TamaVaultItem> Items { get; set; } = new();
}

/// <summary>备份文件里的一条条目。四种类型共用这一个壳，按 <see cref="Type"/> 取对应段落。</summary>
public sealed class TamaVaultItem
{
    public string? Name { get; set; }

    /// <summary>四种类型之一：<c>Login</c> / <c>SecureNote</c> / <c>Card</c> / <c>Identity</c>。</summary>
    public string? Type { get; set; }

    /// <summary>文件夹名（不是 Id——备份要能跨库还原，Id 没有意义）。</summary>
    public string? Folder { get; set; }

    public bool Favorite { get; set; }

    /// <summary>备注；**安全笔记的正文也在这里**（见 CipherMapper.ApplyRemote 的注释）。</summary>
    public string? Notes { get; set; }

    // === v1 扁平载荷：只读，不再写出 ===

    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Totp { get; set; }
    public List<string>? Uris { get; set; }

    // === v2 嵌套载荷 ===

    public TamaVaultLogin? Login { get; set; }
    public TamaVaultCard? Card { get; set; }
    public TamaVaultIdentity? Identity { get; set; }
    public List<string>? Tags { get; set; }
    public List<TamaVaultField>? Fields { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class TamaVaultLogin
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Totp { get; set; }
    public List<string>? Uris { get; set; }
}

public sealed class TamaVaultCard
{
    public string? CardholderName { get; set; }
    public string? Number { get; set; }
    public string? Brand { get; set; }
    public string? ExpMonth { get; set; }
    public string? ExpYear { get; set; }
    public string? Code { get; set; }
}

public sealed class TamaVaultIdentity
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Ssn { get; set; }
    public string? Username { get; set; }
}

public sealed class TamaVaultField
{
    public string? Name { get; set; }
    public string? Value { get; set; }
    public int Type { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>备份文件的常量与序列化设置。</summary>
public static class TamaVaultJson
{
    public const string Format = "tama-json";

    /// <summary>当前写出版本。导入端必须同时认得 1 和它。</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// 导出用：camelCase + 缩进（用户会拿肉眼读这文件）+ 不转义中文
    /// （默认编码器会把"工作"写成 \u5DE5\u4F5C，对本项目的用户完全不可读，逐字节变大）。
    /// </summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 导入用：camelCase + **大小写不敏感**。大小写不敏感是给手改过的文件兜底——
    /// 用户拿记事本改一个字段的大小写，不该导致整份备份读不出来。
    /// </summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
