using System.Text;
using Android.App;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Tama.Avalonia.Android.Platforms;

/// <summary>
/// AndroidKeyStore + 私有 prefs 的生物识别凭据存储（MAUI 版原样搬过来，只把 Context 来源
/// 从 <c>Platform.CurrentActivity</c> 换成 <see cref="AndroidPlatform.Context"/>）。
///
/// 语义：AES-256 密钥生成在 AndroidKeyStore 里（不可导出），只用来加/解密主密码；
/// 密文 <c>"Base64(iv).Base64(ct)"</c> 存 SharedPreferences（应用私有）。
/// 密钥**没有**绑定 SetUserAuthenticationRequired —— 与旧实现一致：指纹验证由 BiometricPrompt
/// 单独弹，验证通过才去解密；密钥本身不参与认证。要绑的话必须连着 prompt 一起改，
/// 否则解密会直接抛 UserNotAuthenticated。
/// </summary>
internal static class BiometricHelper
{
    private const string KeyAlias = "TamaVaultKey";
    private const string KeyStoreName = "AndroidKeyStore";
    private const string PrefName = "tama_biometric";
    private const string PrefEncryptedPassword = "encrypted_password";

    /// <summary>是否有可用于验证的认证器（BIOMETRIC_STRONG 或设备凭据，两者组合查询）。</summary>
    public static bool IsAvailable(Context context)
    {
        try
        {
            var manager = AndroidX.Biometric.BiometricManager.From(context);
            // 0 = BIOMETRIC_SUCCESS。androidx 的绑定里没有对得上的枚举，直接用数值比较
            // （旧实现的注释也是这么记的）。
            var result = manager.CanAuthenticate(
                AndroidX.Biometric.BiometricManager.Authenticators.BiometricStrong
                | AndroidX.Biometric.BiometricManager.Authenticators.DeviceCredential);
            return result == 0;
        }
        catch
        {
            // 某些机型/OEM 在无认证器时会抛异常
            return false;
        }
    }

    public static bool IsConfigured(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefName, FileCreationMode.Private)!;
        return !string.IsNullOrEmpty(prefs.GetString(PrefEncryptedPassword, null));
    }

    /// <summary>确保 KeyStore 里有 AES 密钥（不存在才生成）。重复调用是安全的。</summary>
    public static void SetupKey()
    {
        var keyStore = KeyStore.GetInstance(KeyStoreName)!;
        keyStore.Load(null);

        if (keyStore.ContainsAlias(KeyAlias)) return;

        var keyGenerator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName)!;
        var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeCbc)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingPkcs7)
            .SetKeySize(256)
            .Build();
        keyGenerator.Init(spec);
        keyGenerator.GenerateKey();
    }

    public static string? EncryptPassword(string password)
    {
        try
        {
            SetupKey();

            var keyStore = KeyStore.GetInstance(KeyStoreName)!;
            keyStore.Load(null);
            var secretKey = (ISecretKey?)keyStore.GetKey(KeyAlias, null);
            if (secretKey == null) return null;

            var cipher = Cipher.GetInstance("AES/CBC/PKCS7Padding")!;
            cipher.Init(CipherMode.EncryptMode, secretKey);

            // GetIV()/DoFinal() 的绑定都标了可空，实际 Encrypt 路径必定有值
            var iv = cipher.GetIV()!;
            var encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(password))!;

            return Convert.ToBase64String(iv) + "." + Convert.ToBase64String(encrypted);
        }
        catch
        {
            // 绝不回退成明文
            return null;
        }
    }

    public static string? DecryptPassword(string encryptedData)
    {
        try
        {
            var parts = encryptedData.Split('.');
            if (parts.Length != 2) return null;

            var iv = Convert.FromBase64String(parts[0]);
            var encrypted = Convert.FromBase64String(parts[1]);

            var keyStore = KeyStore.GetInstance(KeyStoreName)!;
            keyStore.Load(null);
            var secretKey = (ISecretKey?)keyStore.GetKey(KeyAlias, null);
            if (secretKey == null) return null;

            var cipher = Cipher.GetInstance("AES/CBC/PKCS7Padding")!;
            cipher.Init(CipherMode.DecryptMode, secretKey, new IvParameterSpec(iv));

            return Encoding.UTF8.GetString(cipher.DoFinal(encrypted)!);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveEncryptedPassword(Context context, string encrypted)
    {
        var prefs = context.GetSharedPreferences(PrefName, FileCreationMode.Private)!;
        // 必须同步 Commit()：Apply() 是异步落盘，解锁后进程被系统杀掉时数据会丢
        // （已由设备日志实证：configured 在下次进程里读不到 → 指纹入口永远起不来）。
        prefs.Edit()!.PutString(PrefEncryptedPassword, encrypted)!.Commit();
    }

    public static string? GetEncryptedPassword(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefName, FileCreationMode.Private)!;
        return prefs.GetString(PrefEncryptedPassword, null);
    }

    public static void ClearEncryptedPassword(Context context)
    {
        var prefs = context.GetSharedPreferences(PrefName, FileCreationMode.Private)!;
        prefs.Edit()!.Remove(PrefEncryptedPassword)!.Commit();
    }
}
