namespace Tama.Core.Contracts;

/// <summary>
/// WebAuthn 标准协议：供浏览器扩展 shim <c>navigator.credentials</c>。
/// 实现：<c>Tama.Services.WebAuthn.WebAuthnService</c>。
///
/// 注意：这一组请求/响应类型同时是**扩展 native-host 的 wire 契约**——
/// 字段名（尤其 <c>clientDataJSON</c> / <c>authenticatorData</c> 的缩写大小写）
/// 被 extensions/tama-webauthn/content-main.js 直接读取，改名会静默破坏扩展。
/// 回归测试见 tests/Tama.Tests/ExtensionBridgeTests.cs。
/// </summary>
public interface IWebAuthnApi
{
    /// <summary>注册：生成/复用密钥对并产出 attestation。</summary>
    Task<WebAuthnCreateResponse> Create(WebAuthnCreateRequest? req);

    /// <summary>断言：用已有凭据签名 challenge。</summary>
    Task<WebAuthnGetResponse> Get(WebAuthnGetRequest? req);

    /// <summary>探测该 rpId 是否有本地凭据——扩展据此决定是否拦截（无匹配时必须放行原生认证器）。</summary>
    Task<WebAuthnProbeResponse> Probe(WebAuthnProbeRequest? req);
}

// === 请求 ===

public sealed record WebAuthnCreateRequest
{
    public string RpId { get; init; } = "";
    public string Challenge { get; init; } = "";
    public string Origin { get; init; } = "";
    public string? UserName { get; init; }
    public string? UserDisplayName { get; init; }
    /// <summary>RP 提供的用户标识（user.id，base64url）。注册时忽略它会导致登录断言的 userHandle 与服务器存储不符而被拒。</summary>
    public string? UserHandle { get; init; }
}

public sealed record WebAuthnGetRequest
{
    public string RpId { get; init; } = "";
    public string Challenge { get; init; } = "";
    public string Origin { get; init; } = "";
    public string? CredentialId { get; init; }
    /// <summary>allowCredentials 中的凭据 Id（base64url）。后端据此选凭据，不再"取最新"。</summary>
    public List<string>? AllowCredentialIds { get; init; }
}

public sealed record WebAuthnProbeRequest
{
    public string RpId { get; init; } = "";
    public List<string>? AllowCredentialIds { get; init; }
}

// === 响应（成功路径无 success 字段；失败返回 Success=false + Reason，故两者可空） ===

public sealed record WebAuthnAttestationData(string ClientDataJSON, string AttestationObject, string AuthenticatorData);

public sealed record WebAuthnAssertionData(string ClientDataJSON, string AuthenticatorData, string Signature, string UserHandle);

public sealed record WebAuthnCreateResponse(
    bool? Success,
    string? Reason,
    string? Id,
    string? RawId,
    string? Type,
    string? PublicKey,
    int? PublicKeyAlgorithm,
    List<string>? Transports,
    WebAuthnAttestationData? Response,
    string? AuthenticatorAttachment,
    string? RpId,
    string? UserName);

public sealed record WebAuthnGetResponse(
    bool? Success,
    string? Reason,
    string? Id,
    string? RawId,
    string? Type,
    WebAuthnAssertionData? Response,
    string? RpId,
    int Counter);

public sealed record WebAuthnProbeResponse(bool HasMatch);
