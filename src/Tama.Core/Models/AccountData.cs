using System.Text;

namespace Tama.Core.Models;

public class AccountData
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Type { get; set; } = "bitwarden";
    public string? ServerUrl { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public string EncryptionKey { get; set; } = string.Empty;
    public string? RawTokenKey { get; set; }
    public string? DerivedEncKey { get; set; }
    public string? DerivedMacKey { get; set; }
    public int KdfIterations { get; set; }
    public string? RawTokenResponse { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public void EncryptSensitiveFields(byte[] key)
    {
        if (!string.IsNullOrEmpty(AccessToken))
            AccessToken = EncryptString(AccessToken, key);
        if (!string.IsNullOrEmpty(RefreshToken))
            RefreshToken = EncryptString(RefreshToken, key);
        if (!string.IsNullOrEmpty(DerivedEncKey))
            DerivedEncKey = EncryptString(DerivedEncKey, key);
        if (!string.IsNullOrEmpty(DerivedMacKey))
            DerivedMacKey = EncryptString(DerivedMacKey, key);
        if (!string.IsNullOrEmpty(EncryptionKey))
            EncryptionKey = EncryptString(EncryptionKey, key);
        if (!string.IsNullOrEmpty(RawTokenKey))
            RawTokenKey = EncryptString(RawTokenKey, key);
        if (!string.IsNullOrEmpty(RawTokenResponse))
            RawTokenResponse = EncryptString(RawTokenResponse, key);
    }

    public void DecryptSensitiveFields(byte[] key)
    {
        if (!string.IsNullOrEmpty(AccessToken) && AccessToken.StartsWith("enc:"))
            AccessToken = DecryptString(AccessToken, key);
        if (!string.IsNullOrEmpty(RefreshToken) && RefreshToken.StartsWith("enc:"))
            RefreshToken = DecryptString(RefreshToken, key);
        if (!string.IsNullOrEmpty(DerivedEncKey) && DerivedEncKey.StartsWith("enc:"))
            DerivedEncKey = DecryptString(DerivedEncKey, key);
        if (!string.IsNullOrEmpty(DerivedMacKey) && DerivedMacKey.StartsWith("enc:"))
            DerivedMacKey = DecryptString(DerivedMacKey, key);
        if (!string.IsNullOrEmpty(EncryptionKey) && EncryptionKey.StartsWith("enc:"))
            EncryptionKey = DecryptString(EncryptionKey, key);
        if (!string.IsNullOrEmpty(RawTokenKey) && RawTokenKey.StartsWith("enc:"))
            RawTokenKey = DecryptString(RawTokenKey, key);
        if (!string.IsNullOrEmpty(RawTokenResponse) && RawTokenResponse.StartsWith("enc:"))
            RawTokenResponse = DecryptString(RawTokenResponse, key);
    }

    private static string EncryptString(string plainText, byte[] key)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        return "enc:" + Convert.ToBase64String(aes.IV) + "." + Convert.ToBase64String(encryptedBytes);
    }

    private static string DecryptString(string encryptedText, byte[] key)
    {
        var data = encryptedText[4..]; // Remove "enc:" prefix
        var parts = data.Split('.');
        var iv = Convert.FromBase64String(parts[0]);
        var encryptedBytes = Convert.FromBase64String(parts[1]);

        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
        return Encoding.UTF8.GetString(decryptedBytes);
    }
}
