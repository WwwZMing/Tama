using Tama.Core.Models;

namespace Tama.Core.Interfaces;

public interface IBitwardenApiClient
{
    Task<BitwardenLoginResponse> LoginAsync(
        string email,
        string masterPassword,
        string? twoFactorCode = null,
        int? twoFactorProvider = null,
        bool newDeviceVerification = false);
    Task<BitwardenSyncResponse> SyncAsync(string accessToken, string encryptionKey, DateTime? lastSync = null);
    Task<BitwardenSyncResponse> SyncAsync(string accessToken, string encKey, string macKey, DateTime? lastSync = null);
    Task<List<BitwardenCipherResponse>> GetCiphersAsync(string accessToken, string encryptionKey);
    Task<BitwardenRefreshResponse?> RefreshTokenAsync(string refreshToken, string? accessToken = null);
    Task<BitwardenCipherResponse?> CreateCipherAsync(string accessToken, string encKeyB64, string macKeyB64, object cipherRequest);
    Task<bool> UpdateCipherAsync(string accessToken, string cipherId, object cipherRequest);
    Task<bool> DeleteCipherAsync(string accessToken, string cipherId);
    Task<bool> TrashCipherAsync(string accessToken, string cipherId);
    Task<bool> RestoreCipherAsync(string accessToken, string cipherId);
    Task<BitwardenFolderResponse?> CreateFolderAsync(string accessToken, string name, byte[] encKey, byte[] macKey);
    Task<bool> UpdateFolderAsync(string accessToken, string folderId, string name, byte[] encKey, byte[] macKey);
    Task<bool> DeleteFolderAsync(string accessToken, string folderId);
}

public class BitwardenLoginResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string KdfType { get; set; } = "0";
    public int KdfIterations { get; set; } = 600000;
    public Dictionary<int, string>? TwoFactorProviders { get; set; }
    public bool TwoFactorRequired => TwoFactorProviders != null && TwoFactorProviders.Count > 0;

    /// <summary>
    /// 这次要的是**"新设备登录验证"的邮箱码**，而不是账号自身的两步验证。
    /// 提交时必须用 <c>newDeviceOtp</c>（不是 <c>twoFactorToken</c>），否则码是对的也登不上。
    /// </summary>
    public bool RequiresNewDeviceVerification { get; set; }

    public string? TwoFactorProviders2 { get; set; }
    public string? RawTokenKey { get; set; }
    public string? DerivedEncKey { get; set; }
    public string? DerivedMacKey { get; set; }
}

public class BitwardenSyncResponse
{
    public List<BitwardenCipherResponse> Ciphers { get; set; } = new();
    public List<BitwardenFolderResponse> Folders { get; set; } = new();
    public BitwardenProfileResponse? Profile { get; set; }

    /// <summary>
    /// 本次是否为全量同步。**当前恒为 true**：Bitwarden 的 /api/sync 只有全量快照语义
    /// （官方客户端同样是全量拉 + WebSocket 推增量，`lastSync` 是客户端记账字段而非请求参数）。
    /// 保留这个字段是为了给"孤儿清理"留一个**显式开关**：只有确认 payload 是完整快照时，
    /// 才允许删掉本地那些"服务器上不存在"的条目。哪天真的支持增量了，这里必须跟着变。
    /// </summary>
    public bool IsFullSync { get; set; }

    /// <summary>组织 cipher 解密失败数。&gt;0 提示可能发生了组织密钥轮换（org key rotation），应强制全量重拉。</summary>
    public int DecryptFailures { get; set; }

    /// <summary>
    /// 服务器 cipher 的最大 RevisionDate。**只用于本地记账与游标下限**（防止游标回退），
    /// 不要把它当成"下次请求的过滤参数"——服务端没有这个能力。
    /// </summary>
    public DateTime? MaxRevisionDate { get; set; }
}

public class BitwardenCipherResponse
{
    public string Id { get; set; } = string.Empty;
    public int Type { get; set; }
    public string? Name { get; set; }
    public string? Notes { get; set; }
    public bool Favorite { get; set; }
    public string? FolderId { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime RevisionDate { get; set; }
    public DateTime? DeletedDate { get; set; }
    public string? OrganizationId { get; set; }
    public string? Key { get; set; }
    public BitwardenLoginData? Login { get; set; }
    public BitwardenCardData? Card { get; set; }
    public BitwardenIdentityData? Identity { get; set; }
    public BitwardenSecureNoteData? SecureNote { get; set; }
    public List<BitwardenFido2Credential>? Fido2Credentials { get; set; }
    public List<BitwardenFieldData>? Fields { get; set; }
}

public class BitwardenFieldData
{
    public string? Name { get; set; }
    public string? Value { get; set; }
    public int Type { get; set; }
    public bool Hidden { get; set; }
}

public class BitwardenFido2Credential
{
    public string? CredentialId { get; set; }
    public string? KeyType { get; set; }
    public string? KeyAlgorithm { get; set; }
    public string? KeyCurve { get; set; }
    public string? KeyValue { get; set; }
    public string? RpId { get; set; }
    public string? RpName { get; set; }
    public string? UserName { get; set; }
    public string? UserHandle { get; set; }
    public string? UserDisplayName { get; set; }
    public int Counter { get; set; }
    public bool Discoverable { get; set; }
    public DateTime CreationDate { get; set; }
}

public class BitwardenLoginData
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public List<BitwardenUriData> Uris { get; set; } = new();
    public string? Totp { get; set; }
}

public class BitwardenUriData
{
    public string? Uri { get; set; }
}

public class BitwardenCardData
{
    public string? CardholderName { get; set; }
    public string? Number { get; set; }
    public string? Brand { get; set; }
    public string? ExpMonth { get; set; }
    public string? ExpYear { get; set; }
    public string? Code { get; set; }
}

public class BitwardenIdentityData
{
    public string? FirstName { get; set; }
    public string? MiddleName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Company { get; set; }
    public string? Ssn { get; set; }
    public string? Username { get; set; }
    public string? Title { get; set; }
}

public class BitwardenSecureNoteData
{
    public string? Text { get; set; }
}

public class BitwardenFolderResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class BitwardenProfileResponse
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Premium { get; set; }
    public string? Key { get; set; }
    public string? PrivateKey { get; set; }
    public List<BitwardenOrganizationKey>? Organizations { get; set; }
}

public class BitwardenOrganizationKey
{
    public string Id { get; set; } = string.Empty;
    public string? Key { get; set; }
}

public class BitwardenRefreshResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
}
