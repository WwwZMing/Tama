using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Tama.Services.Auth;

/// <summary>
/// 纯离线可信设备配对的编码与最小加密原语。
///
/// 二维码里只放“用一次性配对码加密后的保险库密钥”；配对码本身不出现在二维码里，
/// 必须由用户在扫码设备上手动输入。这样单独拍到二维码的人无法直接拿走恢复能力。
/// </summary>
internal static class OfflineTrustedDeviceCodec
{
    private const int PairingCodeBytes = 10;
    private const int PairingIterations = 310_000;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string CreatePairingCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(PairingCodeBytes);
        try
        {
            return FormatCode(EncodeBase32(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string NormalizePairingCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";

        var builder = new StringBuilder(code.Length);
        foreach (var character in code)
        {
            if (character is '-' or ' ') continue;
            var normalized = char.ToUpperInvariant(character);
            if (normalized == '0') normalized = 'O';
            if (normalized == '1') normalized = 'I';
            if (Base32Alphabet.Contains(normalized, StringComparison.Ordinal))
                builder.Append(normalized);
        }

        return builder.ToString();
    }

    public static string FormatCode(string normalizedCode)
    {
        var normalized = NormalizePairingCode(normalizedCode);
        if (normalized.Length != 16)
            throw new CryptographicException("配对码格式无效");

        return string.Join('-', Enumerable.Range(0, 4)
            .Select(i => normalized.Substring(i * 4, 4)));
    }

    public static SealedPairingKey SealVaultKey(
        byte[] vaultKey, string pairingCode, string vaultId, string offerId)
    {
        if (vaultKey.Length != KeySize || string.IsNullOrWhiteSpace(vaultId)
            || string.IsNullOrWhiteSpace(offerId))
            throw new CryptographicException("配对密钥数据无效");

        var salt = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var kek = DerivePairingKey(pairingCode, salt);
        try
        {
            var ciphertext = new byte[vaultKey.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(kek, TagSize);
            aes.Encrypt(nonce, vaultKey, ciphertext, tag, PairingAad(vaultId, offerId));

            return new SealedPairingKey(
                Convert.ToBase64String(salt),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String([.. ciphertext, .. tag]));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public static bool TryOpenVaultKey(
        SealedPairingKey envelope, string pairingCode, string vaultId, string offerId,
        out byte[] vaultKey)
    {
        vaultKey = [];
        try
        {
            var salt = Convert.FromBase64String(envelope.Salt);
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var combined = Convert.FromBase64String(envelope.Ciphertext);
            if (salt.Length < 16 || nonce.Length != NonceSize || combined.Length != KeySize + TagSize)
                return false;

            var kek = DerivePairingKey(pairingCode, salt);
            try
            {
                vaultKey = new byte[KeySize];
                using var aes = new AesGcm(kek, TagSize);
                aes.Decrypt(
                    nonce,
                    combined.AsSpan(0, KeySize),
                    combined.AsSpan(KeySize, TagSize),
                    vaultKey,
                    PairingAad(vaultId, offerId));
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(vaultKey);
            vaultKey = [];
            return false;
        }
    }

    public static string ComputeMutualCode(byte[] vaultKey, string vaultId, string offerId)
    {
        var input = Encoding.UTF8.GetBytes($"Tama.OfflinePairing.Mutual.v1|{vaultId}|{offerId}");
        var mac = HMACSHA256.HashData(vaultKey, input);
        try
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(mac);
            return (value % 1_000_000).ToString("D6");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    /// <summary>
    /// 二维码不需要暴露原始 VaultId；两边只使用这个稳定、不可逆的标签作为 AEAD 绑定值。
    /// </summary>
    public static string ComputeVaultTag(string vaultId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(vaultId));
        return Convert.ToBase64String(digest)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string EncodeBase64Url(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>
    /// 二维码里的设备名使用确定的 Base64Url 编码。以前生成端直接写 URI 转义文本、
    /// 读取端却按 Base64Url 解码，非 ASCII 机器名会变成替换字符。
    /// </summary>
    public static string EncodeDeviceName(string value)
        => EncodeBase64Url(value);

    public static string DecodeDeviceName(string value)
        => DecodeBase64Url(value);

    public static string DecodeBase64Url(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CryptographicException("二维码内容无效");

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private static byte[] DerivePairingKey(string pairingCode, byte[] salt)
    {
        var normalized = NormalizePairingCode(pairingCode);
        if (normalized.Length != 16)
            throw new CryptographicException("配对码应为 16 个字符");

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalized),
            salt,
            PairingIterations,
            HashAlgorithmName.SHA256,
            KeySize);
    }

    private static byte[] PairingAad(string vaultId, string offerId)
        => Encoding.UTF8.GetBytes($"Tama.OfflinePairing.v1|{vaultId}|{offerId}");

    private static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);

        return output.ToString();
    }

    internal sealed record SealedPairingKey(string Salt, string Nonce, string Ciphertext);
}
