using Android.App;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Text;

namespace Tama.Platforms.Droid;

public static class BiometricHelper
{
    private const string KeyAlias = "TamaVaultKey";
    private const string KeyStoreName = "AndroidKeyStore";
    private const string PrefName = "tama_biometric";
    private const string PrefEncryptedPassword = "encrypted_password";

    public static bool IsAvailable(Activity activity)
    {
        try
        {
            // AndroidX.Biometric not available on net11.0 — use reflection
            var service = activity.GetSystemService("biometric");
            if (service == null) return false;

            var canAuthenticate = service.GetType().GetMethod("CanAuthenticate",
                new[] { typeof(int) });
            if (canAuthenticate == null) return false;

            // BiometricManager.Authenticators.BiometricStrong = 15
            var result = (int)canAuthenticate.Invoke(service, new object[] { 15 })!;
            return result == 0; // BiometricManager.BiometricSuccess
        }
        catch
        {
            return false;
        }
    }

    public static bool IsConfigured(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefName, FileCreationMode.Private)!;
        return !string.IsNullOrEmpty(prefs.GetString(PrefEncryptedPassword, null));
    }

    public static void SetupKey()
    {
        var keyStore = KeyStore.GetInstance(KeyStoreName);
        keyStore.Load(null);

        if (!keyStore.ContainsAlias(KeyAlias))
        {
            var keyGenerator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName);
            var spec = new KeyGenParameterSpec.Builder(KeyAlias,
                    KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeCbc)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingPkcs7)
                .SetKeySize(256)
                .Build();
            keyGenerator.Init(spec);
            keyGenerator.GenerateKey();
        }
    }

    public static string EncryptPassword(string password)
    {
        var keyStore = KeyStore.GetInstance(KeyStoreName);
        keyStore.Load(null);
        var secretKey = (ISecretKey)keyStore.GetKey(KeyAlias, null)!;

        var cipher = Cipher.GetInstance("AES/CBC/PKCS7Padding");
        cipher.Init(CipherMode.EncryptMode, secretKey);

        var iv = cipher.GetIV();
        var encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(password));

        return $"{Convert.ToBase64String(iv)}.{Convert.ToBase64String(encrypted)}";
    }

    public static string? DecryptPassword(string encryptedData)
    {
        try
        {
            var parts = encryptedData.Split('.');
            if (parts.Length != 2) return null;

            var iv = Convert.FromBase64String(parts[0]);
            var encrypted = Convert.FromBase64String(parts[1]);

            var keyStore = KeyStore.GetInstance(KeyStoreName);
            keyStore.Load(null);
            var secretKey = (ISecretKey)keyStore.GetKey(KeyAlias, null)!;

            var cipher = Cipher.GetInstance("AES/CBC/PKCS7Padding");
            var spec = new IvParameterSpec(iv);
            cipher.Init(CipherMode.DecryptMode, secretKey, spec);

            var decrypted = cipher.DoFinal(encrypted);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveEncryptedPassword(Context context, string encryptedPassword)
    {
        var prefs = context.GetSharedPreferences(PrefName, FileCreationMode.Private)!;
        // 必须同步 Commit()：Apply() 异步落盘，解锁后进程被划掉/系统杀时数据丢失
        // （configured 在下次进程里读不到 → 指纹解锁永远起不来，已由设备日志实证）
        prefs.Edit().PutString(PrefEncryptedPassword, encryptedPassword).Commit();
    }

    public static string? GetEncryptedPassword(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefName, FileCreationMode.Private)!;
        return prefs.GetString(PrefEncryptedPassword, null);
    }

    public static void ClearEncryptedPassword(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefName, FileCreationMode.Private)!;
        prefs.Edit().Remove(PrefEncryptedPassword).Commit();
    }
}
