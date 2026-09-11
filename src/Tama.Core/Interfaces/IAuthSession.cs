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
    void Lock();

    /// <summary>
    /// 距上次主密码验证（setup / 用密码解锁）是否已超过 72 小时。
    /// 为 true 时指纹/生物识别解锁被拒绝，必须重新输入主密码（防止生物识别长期无监督使用）。
    /// </summary>
    bool RequireMasterPasswordRecheck();
}
