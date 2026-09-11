using System.Security.Cryptography;
using System.Text;

namespace Tama.Services.Crypto;

/// <summary>
/// 共享密钥派生工具。**只抽算法（PBKDF2-SHA256），不碰参数**——迭代次数/盐由调用方决定：
/// Bitwarden 的迭代参数是协议规定的（prelogin 下发，如 600000），绝不能在这里改默认值。
/// 历史上 VaultCryptoService 与 BitwardenApiClient 各自实现了一份，行为漂移风险高。
/// </summary>
public static class KeyDerivation
{
    /// <summary>PBKDF2-SHA256（密码为 UTF-8 字符串）。输出默认 32 字节。</summary>
    public static byte[] Pbkdf2Sha256(string password, byte[] salt, int iterations, int outputLength = 32)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            outputLength);

    /// <summary>PBKDF2-SHA256（密码为预编码字节，如 Bitwarden HashPassword 第二层以 passwordBytes 为盐）。</summary>
    public static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations, int outputLength = 32)
        => Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            outputLength);
}
