using System.Security.Cryptography;
using System.Text;

namespace Tama.Services.Bitwarden;

public static class BitwardenCrypto
{
    public static (byte[] encKey, byte[] macKey) StretchMasterKey(byte[] masterKey)
    {
        var encKey = HkdfExpand(masterKey, Encoding.UTF8.GetBytes("enc"), 32);
        var macKey = HkdfExpand(masterKey, Encoding.UTF8.GetBytes("mac"), 32);
        return (encKey, macKey);
    }

    private static byte[] HkdfExpand(byte[] prk, byte[] info, int outputLength)
    {
        var hashLen = 32;
        var n = (outputLength + hashLen - 1) / hashLen;
        var result = new byte[outputLength];
        var previous = Array.Empty<byte>();
        using var hmac = new HMACSHA256(prk);
        for (var i = 1; i <= n; i++)
        {
            var input = new byte[previous.Length + info.Length + 1];
            Buffer.BlockCopy(previous, 0, input, 0, previous.Length);
            Buffer.BlockCopy(info, 0, input, previous.Length, info.Length);
            input[^1] = (byte)i;
            previous = hmac.ComputeHash(input);
            Buffer.BlockCopy(previous, 0, result, (i - 1) * hashLen, Math.Min(hashLen, outputLength - (i - 1) * hashLen));
        }
        return result;
    }

    public static (byte[] encKey, byte[] macKey) SplitKey(byte[] key)
    {
        if (key.Length == 64)
            return (key[..32], key[32..]);
        if (key.Length == 32)
            return (key, Array.Empty<byte>());
        throw new ArgumentException($"Invalid key length: {key.Length}");
    }

    public static byte[]? Decrypt(string? encrypted, byte[] encKey, byte[] macKey)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;

        var dotIndex = encrypted.IndexOf('.');
        if (dotIndex < 0) return null;

        var typeStr = encrypted[..dotIndex];
        var payload = encrypted[(dotIndex + 1)..];

        if (typeStr != "2") return null;

        var parts = payload.Split('|');
        if (parts.Length < 2) return null;

        var iv = Convert.FromBase64String(parts[0]);
        var ct = Convert.FromBase64String(parts[1]);
        var mac = parts.Length > 2 ? Convert.FromBase64String(parts[2]) : null;

        if (macKey.Length > 0 && mac != null)
        {
            var macData = iv.Concat(ct).ToArray();
            using var hmac = new HMACSHA256(macKey);
            var computedMac = hmac.ComputeHash(macData);
            if (!CryptographicOperations.FixedTimeEquals(computedMac, mac))
            {
                throw new CryptographicException("HMAC verification failed: ciphertext integrity check failed");
            }
        }

        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ct, 0, ct.Length);
    }

    public static string? DecryptString(string? encrypted, byte[] encKey, byte[] macKey)
    {
        var bytes = Decrypt(encrypted, encKey, macKey);
        return bytes != null ? Encoding.UTF8.GetString(bytes) : null;
    }

    public static byte[] GenerateCipherKey()
    {
        var key = new byte[64];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public static string EncryptString(string plaintext, byte[] encKey, byte[] macKey)
    {
        return Encrypt(Encoding.UTF8.GetBytes(plaintext), encKey, macKey);
    }

    public static string Encrypt(byte[] data, byte[] encKey, byte[] macKey)
    {
        var iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var ct = encryptor.TransformFinalBlock(data, 0, data.Length);

        var mac = Array.Empty<byte>();
        if (macKey.Length > 0)
        {
            var macData = iv.Concat(ct).ToArray();
            using var hmac = new HMACSHA256(macKey);
            mac = hmac.ComputeHash(macData);
        }

        var ivB64 = Convert.ToBase64String(iv);
        var ctB64 = Convert.ToBase64String(ct);
        var macB64 = mac.Length > 0 ? Convert.ToBase64String(mac) : null;

        return macB64 != null
            ? $"2.{ivB64}|{ctB64}|{macB64}"
            : $"2.{ivB64}|{ctB64}";
    }

    public static string EncryptCipherKey(byte[] cipherKey, byte[] userEncKey, byte[] userMacKey)
    {
        return Encrypt(cipherKey, userEncKey, userMacKey);
    }
}
