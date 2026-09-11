namespace Tama.Core.Contracts;

/// <summary>
/// 通行密钥：列表、启用、删除、外部导入。
/// 实现：<c>Tama.Services.Passkey.PasskeyService</c>。
///
/// **创建不在这里**（2026-09-13 删掉了"手填域名创建"）：通行密钥由**网站发起**——
/// 页面调 <c>navigator.credentials.create()</c>，浏览器扩展（extensions/tama-webauthn）拦截后
/// 交给 <see cref="IWebAuthnApi.Create"/> 生成并落库。本接口只管"已经存在于某处的凭据"：
/// 列出、把 Bitwarden 的凭据（同步缓存 / JSON 导出）转成本机可用、删除。
/// 早先那套 <c>CreateRequest</c>/<c>Authenticate</c>/<c>Save</c> 是 React 时代的"自闭环"演示
/// （自己造挑战、自己签、没有 RP 参与），产出的凭据任何网站都不认识，已整体删除。
/// </summary>
public interface IPasskeyApi
{
    /// <summary>平台是否具备通行密钥能力（Windows Hello / Android Credential Manager）。</summary>
    bool Available();

    /// <summary>
    /// 已保存的通行密钥列表：本机**可用**凭据 + Bitwarden 同步来的、还没启用的那些
    /// （后者 <see cref="PasskeyEntryDto.Source"/> = "bitwarden"，只有公钥元数据，不能用来登录）。
    /// </summary>
    Task<List<PasskeyEntryDto>> List(string? accountId = null);

    /// <summary>按凭据 Id 删除（只删本机那一份；Bitwarden 那边的条目不受影响）。</summary>
    Task Delete(string credentialId);

    /// <summary>
    /// 导入 Bitwarden 的**未加密 JSON 导出**（`items[].login.fido2Credentials[]`）。
    /// 导出文件里的 `keyValue` 就是私钥本身（base64url 的 PKCS#8），所以导入后即成为**本机可用**凭据。
    /// 幂等（已存在的跳过）、逐条容错（单条坏不影响其余），结果里报数。
    /// 加密导出（`"encrypted": true`）读不出私钥 → 抛 <c>TamaException</c> 并提示换一种导出方式。
    /// </summary>
    Task<PasskeyImportResult> ImportFromBitwardenJson(byte[] file);

    /// <summary>
    /// 把 Bitwarden **同步缓存里已解密**的通行密钥转成本机可用凭据（幂等，可反复点）。
    /// <paramref name="credentialId"/> 非空时只启用那一枚。
    /// </summary>
    Task<PasskeyImportResult> AdoptSyncedPasskeys(string? credentialId = null);
}

/// <summary>通行密钥列表项。</summary>
public sealed record PasskeyEntryDto(
    string CredentialId,
    string RpId,
    string? RpName,
    string? UserName,
    string? UserHandle,
    int Counter,
    string Source);

/// <summary>
/// 导入 / 启用结果。Imported = 新增为可用凭据；Skipped = 已存在（幂等重复）；
/// Failed = 单条解析不了（不中断其余），原因在 <see cref="Warnings"/> 里。
/// </summary>
public sealed record PasskeyImportResult(int Imported, int Skipped, int Failed, List<string> Warnings);
