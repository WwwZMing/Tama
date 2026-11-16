namespace Tama.Core.Interfaces;

public interface IVaultCrypto
{
    byte[] GenerateSalt();
    byte[] DeriveHash(string password, byte[] salt, int iterations = 100_000);
    byte[] DeriveKey(string password, byte[] salt, int iterations = 100_000);
    bool VerifyPassword(string password, byte[] salt, byte[] expectedHash);
}

public interface IAuthSession
{
    bool IsSetup { get; }
    bool IsUnlocked { get; }
    string? Password { get; }
    Task<bool> IsSetupAsync();
    Task SetupAsync(string password);
    Task<bool> UnlockAsync(string password);

    /// <summary>
    /// 已由应急份额或受信设备解出保险库密钥后接管解锁。新密码只更新密钥包装，
    /// 不重加密数据库；传入 null 表示暂不更换主密码。
    /// </summary>
    Task ApplyVaultKeyAsync(byte[] vaultKey, string? newPassword = null);

    /// <summary>当前保存的设备解锁凭据是否已经绑定保险库密钥（v2），而不是旧版缓存主密码。</summary>
    bool HasModernDeviceCredential { get; }

    void Lock();

    /// <summary>
    /// 距上次主密码验证（setup / 用密码解锁）是否已超过 72 小时。
    /// 为 true 时指纹/生物识别解锁被拒绝，必须重新输入主密码（防止生物识别长期无监督使用）。
    /// </summary>
    bool RequireMasterPasswordRecheck();
}
