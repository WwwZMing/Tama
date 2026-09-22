using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tama.Core.Formats;

/// <summary>
/// Bitwarden 的**未加密个人保险库导出**格式——用来把 Tama 的东西搬进 Bitwarden
/// （官网「工具 → 导入数据」选 <c>Bitwarden (json)</c>，或 <c>bw import bitwardenjson</c>）。
///
/// 形状照抄 bitwarden/clients 的规范定义，不是照着文档猜的：
///   · 顶层：<c>libs/vault-export-core</c> 的 <c>BitwardenUnEncryptedIndividualJsonExport</c>
///     （<c>encrypted: false</c> + <c>folders[]</c> + <c>items[]</c>）
///   · 条目：<c>libs/common/src/models/export/cipher.export.ts</c> 的 <c>CipherExport</c>
///   · 判分与文件夹绑定：<c>libs/importer/src/importers/bitwarden/bitwarden-json-importer.ts</c>
///
/// ⚠ 三个从源码里读出来、光看文档一定会踩的坑：
///   1. <c>encrypted</c> 必须是 <c>false</c>——导入器见到加密的直接抛
///      "Data is encrypted. Use BitwardenEncryptedJsonImporter instead."；
///   2. 自定义字段用的是 <c>type</c>（0=文本 1=隐藏 2=布尔）+ <c>linkedId</c>，
///      **没有 <c>hidden</c> 这个字段**（见 field.export.ts）。本地模型两个都有，导出时得换算；
///   3. 安全笔记必须带上 <c>secureNote: {"type":0}</c>——导入器的 <c>toView</c> 只在
///      <c>req.secureNote != null</c> 时才认它是笔记。正文本身在 <c>notes</c> 里。
/// </summary>
public sealed class BitwardenVaultFile
{
    /// <summary>必须为 false。加密导出走的是另一个导入器，这里给 true 会被直接拒绝。</summary>
    public bool Encrypted { get; set; }

    public List<BitwardenFolderJson> Folders { get; set; } = new();
    public List<BitwardenItemJson> Items { get; set; } = new();
}

public sealed class BitwardenFolderJson
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// 一条条目。<c>type</c> 用 Bitwarden 的 CipherType：1=登录 2=安全笔记 3=银行卡 4=身份信息
/// （与本地枚举同值，直接强转即可）。
/// </summary>
public sealed class BitwardenItemJson
{
    public string? Id { get; set; }

    /// <summary>组织条目才用；个人库一律 null。</summary>
    public string? OrganizationId { get; set; }

    /// <summary>指向 <see cref="BitwardenVaultFile.Folders"/> 里某一项的 <c>id</c>；不匹配就落"未分类"。</summary>
    public string? FolderId { get; set; }

    public int Type { get; set; }

    /// <summary>重新输入主密码提示等级，0 = 不提示（CipherRepromptType.None）。</summary>
    public int Reprompt { get; set; }

    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public bool Favorite { get; set; }

    public List<BitwardenFieldJson>? Fields { get; set; }
    public BitwardenLoginJson? Login { get; set; }
    public BitwardenSecureNoteJson? SecureNote { get; set; }
    public BitwardenCardJson? Card { get; set; }
    public BitwardenIdentityJson? Identity { get; set; }

    public DateTime? CreationDate { get; set; }
    public DateTime? RevisionDate { get; set; }
}

/// <summary>自定义字段。注意是 <c>type</c> 不是 <c>hidden</c>（见类注释的坑 2）。</summary>
public sealed class BitwardenFieldJson
{
    public string? Name { get; set; }
    public string? Value { get; set; }

    /// <summary>FieldType：0=文本 1=隐藏 2=布尔。</summary>
    public int Type { get; set; }

    /// <summary>内置字段关联（用户名/密码等），Tama 没有这个概念，恒 null。</summary>
    public int? LinkedId { get; set; }
}

public sealed class BitwardenLoginJson
{
    public List<BitwardenUriJson>? Uris { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Totp { get; set; }
}

public sealed class BitwardenUriJson
{
    public string? Uri { get; set; }

    /// <summary>URIMatchType；null = 用 Bitwarden 的默认（"base domain"）。</summary>
    public int? Match { get; set; }
}

/// <summary>安全笔记。正文在 <see cref="BitwardenItemJson.Notes"/>，这里只是个类型标记。</summary>
public sealed class BitwardenSecureNoteJson
{
    /// <summary>SecureNoteType：0=通用。</summary>
    public int Type { get; set; }
}

public sealed class BitwardenCardJson
{
    public string? CardholderName { get; set; }
    public string? Brand { get; set; }
    public string? Number { get; set; }
    public string? ExpMonth { get; set; }
    public string? ExpYear { get; set; }
    public string? Code { get; set; }
}

/// <summary>
/// 身份信息。Bitwarden 把地址拆成 address1/2/3 + city/state/postalCode/country，
/// 而本地只有一个 <c>Address</c> —— 导出时整段塞进 address1（**不拆分**：
/// 猜错拆分位置等于篡改用户地址）。反向（Bitwarden → 本地）在 CipherMapper.JoinAddress。
/// </summary>
public sealed class BitwardenIdentityJson
{
    public string? Title { get; set; }
    public string? FirstName { get; set; }
    public string? MiddleName { get; set; }
    public string? LastName { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Company { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Ssn { get; set; }
    public string? Username { get; set; }
    public string? PassportNumber { get; set; }
    public string? LicenseNumber { get; set; }
}

public static class BitwardenVaultJson
{
    /// <summary>
    /// 与 <see cref="TamaVaultJson.WriteOptions"/> 同一套设置：camelCase、缩进（这文件用户要能读）、
    /// 不转义中文。null 不写出——Bitwarden 的 toView 对缺字段一律当 undefined，不会因此报错。
    /// </summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// FieldType 换算：本地模型同时有 <c>Type</c>（来自 Bitwarden API）和 <c>Hidden</c>（历史遗留），
    /// 而导出格式只认 <c>type</c>。以 <c>Type</c> 为准，它缺失（0=文本）但 <c>Hidden</c> 为真时兜底成 1，
    /// 否则用户标了"隐藏"的字段会在 Bitwarden 里变成明文展示。
    /// </summary>
    public static int FieldType(int type, bool hidden)
        => type is 1 or 2 ? type : (hidden ? 1 : 0);
}
