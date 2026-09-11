namespace Tama.Core.Models;

/// <summary>
/// 本地 FIDO2（软通行密钥）凭据：ECDSA P-256 密钥对在客户端生成，
/// 私钥用主密码派生密钥（DatabaseKeyService）AES-GCM 加密后存库，明文私钥永不离开后端。
/// </summary>
public class PasskeyCredentialRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>credential ID = 公钥 SHA-256 的 base64url（WebAuthn 惯例）</summary>
    public string CredentialId { get; set; } = string.Empty;
    public string RpId { get; set; } = string.Empty;
    public string? RpName { get; set; }
    public string? UserName { get; set; }
    public string? UserHandle { get; set; }
    /// <summary>ECDSA P-256 公钥，base64（DER SubjectPublicKeyInfo）</summary>
    public string PublicKey { get; set; } = string.Empty;
    /// <summary>私钥密文，格式 enc:nonce.ct.tag（AES-GCM，DatabaseKeyService 密钥）</summary>
    public string EncryptedPrivateKey { get; set; } = string.Empty;
    public int Counter { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
