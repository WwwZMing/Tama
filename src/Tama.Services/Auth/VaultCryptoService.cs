using System.Security.Cryptography;
using Tama.Core.Interfaces;
using Tama.Services.Crypto;

namespace Tama.Services.Auth;

public class VaultCryptoService : IVaultCrypto
{
    public byte[] GenerateSalt()
    {
        var salt = new byte[32];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    // 密码哈希与加密密钥派生算法相同（PBKDF2-SHA256, 32 字节输出），
    // 语义不同（哈希用于验证密码、密钥用于加密库）故保留两个入口，共享同一实现。
    public byte[] DeriveHash(string password, byte[] salt, int iterations = 100_000)
        => KeyDerivation.Pbkdf2Sha256(password, salt, iterations);

    public byte[] DeriveKey(string password, byte[] salt, int iterations = 100_000)
        => KeyDerivation.Pbkdf2Sha256(password, salt, iterations);

    public bool VerifyPassword(string password, byte[] salt, byte[] expectedHash)
    {
        var hash = DeriveHash(password, salt);
        return CryptographicOperations.FixedTimeEquals(hash, expectedHash);
    }
}
