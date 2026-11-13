using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Tama.Core.Exceptions;

namespace Tama.Avalonia.Android.Platforms;

/// <summary>
/// AndroidKeyStore + 私有 prefs 的生物识别凭据存储。
///
/// 语义：AES-256 密钥生成在 AndroidKeyStore 里（不可导出），只用来加/解密主密码；
/// 密文 <c>"Base64(iv).Base64(ct)"</c> 存 SharedPreferences（应用私有）。
/// 密钥**没有**绑定 SetUserAuthenticationRequired —— 与旧实现一致：指纹验证由 BiometricPrompt
/// 单独弹，验证通过才去解密；密钥本身不参与认证。要绑的话必须连着 prompt 一起改，
/// 否则解密会直接抛 UserNotAuthenticated。
///
/// ⚠ 2026-11-13 修正两处「一踩就静默」的写法（用户报「提示我密钥不可用」）：
///   1. **不要把 GetKey 的结果强转成 ISecretKey**：Xamarin 绑定返回的是 <see cref="IKey"/> 包装对象，
///      C# 强转转不过去就抛 InvalidCastException，而这里原来把异常吞成 null，
///      界面上只剩一句"系统密钥不可用"。现在直接把 IKey 交给 <c>Cipher.Init</c>
///      （它本来就是 java.security.Key 参数），**类型转换这一步完全不需要**。
///   2. **PKCS7Padding 与 PKCS5Padding 都要试**：对 AES 两者是同一件事（块长 16），
///      但各 Android 版本对这两个名字的注册不一致，写死一个会在某些机型上直接
///      NoSuchPaddingException。现在按顺序试，两个都不行才报错。
///
/// 失败不再静默：加密失败会抛 <see cref="TamaException"/>（消息里带失败的步骤与异常类型），
/// 设置页会把 ex.Message 显示出来——否则"密钥不可用"这种提示永远查不出原因。
/// </summary>
internal static class BiometricHelper
{
    private const string KeyAlias = "TamaVaultKey";
    private const string KeyStoreName = "AndroidKeyStore";
    private const string PrefName = "tama_biometric";
    private const string PrefEncryptedPassword = "encrypted_password";

    /// <summary>AES/CBC 的两套 padding 名字（等价，见类注释）。</summary>
    private static readonly string[] CbcTransformations =
    {
        "AES/CBC/PKCS7Padding",
        "AES/CBC/PKCS5Padding",
    };

    /// <summary>是否有可用于验证的认证器（BIOMETRIC_STRONG 或设备凭据，两者组合查询）。</summary>
    public static bool IsAvailable(Context context)
    {
        try
        {
            var manager = AndroidX.Biometric.BiometricManager.From(context);
            // 0 = BIOMETRIC_SUCCESS。androidx 的绑定里没有对得上的枚举，直接用数值比较。
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
    public static void SetupKey() => _ = GetOrCreateKey();

    /// <summary>
    /// 加密主密码。失败**抛异常**（消息可直接展示），绝不返回明文、也不静默返回 null——
    /// null 只留给"密码本身是空的"这种调用方错误。
    /// </summary>
    public static string EncryptPassword(string password)
    {
        try
        {
            return EncryptOnce(password);
        }
        catch (Exception first)
        {
            // 走到这里说明密钥或算法有问题。最可能的一种：别名存在，但是**旧版本用别的分组模式
            // （例如 GCM）建的键**——KeyStore.ContainsAlias 为真、Init 却抛 InvalidKeyException。
            // 加密都失败了，库里也不可能有对应的旧密文，所以删掉重建一次再试是安全的。
            Serilog.Log.Warning(first, "Biometric encrypt failed, regenerating keystore key once");
            try { DeleteKey(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Delete keystore key failed"); }

            try
            {
                return EncryptOnce(password);
            }
            catch (Exception second)
            {
                Serilog.Log.Error(second, "Biometric encrypt failed after regenerating key");
                throw new TamaException($"系统密钥不可用（{Describe(second)}）", second);
            }
        }
    }

    private static string EncryptOnce(string password)
    {
        var key = GetOrCreateKey();

        var cipher = NewCipher();
        cipher.Init(CipherMode.EncryptMode, key);

        // GetIV()/DoFinal() 的绑定都标了可空，实际加密路径必定有值
        var iv = cipher.GetIV()!;
        var encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(password))!;

        return Convert.ToBase64String(iv) + "." + Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// 解密。<b>失败一律返回 null</b>（接口约定）：调用方据此判断"保存的凭据已失效"并清掉它。
    /// 这里**不能**抛异常——它跑在解锁路径上，抛出去会把"用主密码解锁"一起带崩。
    /// </summary>
    public static string? DecryptPassword(string encryptedData)
    {
        try
        {
            var parts = encryptedData.Split('.');
            if (parts.Length != 2) return null;

            var iv = Convert.FromBase64String(parts[0]);
            var encrypted = Convert.FromBase64String(parts[1]);

            var key = GetOrCreateKey();

            var cipher = NewCipher();
            cipher.Init(CipherMode.DecryptMode, key, new IvParameterSpec(iv));

            return Encoding.UTF8.GetString(cipher.DoFinal(encrypted)!);
        }
        catch (Exception ex)
        {
            // 密文坏了 / 密钥换了 / 主密码改过：都归到"凭据失效"
            Serilog.Log.Warning(ex, "Biometric decrypt failed");
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

    // === KeyStore ===

    /// <summary>
    /// 取（必要时创建）密钥，返回 <see cref="IKey"/> 本身交给 Cipher。
    /// 不做 ISecretKey 强转——见类注释第 1 条。
    /// </summary>
    private static IKey GetOrCreateKey()
    {
        try
        {
            var keyStore = KeyStore.GetInstance(KeyStoreName)!;
            keyStore.Load(null);

            if (!keyStore.ContainsAlias(KeyAlias)) GenerateKey(keyStore);

            var key = keyStore.GetKey(KeyAlias, null);
            if (key == null) throw new TamaException("AndroidKeyStore 没有生成出 TamaVaultKey");
            return key;
        }
        catch (TamaException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TamaException($"访问 AndroidKeyStore 失败（{Describe(ex)}）", ex);
        }
    }

    private static void GenerateKey(KeyStore keyStore)
    {
        var keyGenerator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName)!;
        var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeCbc)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingPkcs7)
            .SetKeySize(256)
            .Build();
        keyGenerator.Init(spec);
        keyGenerator.GenerateKey();
        Serilog.Log.Information("Biometric AES key created in AndroidKeyStore");
    }

    private static void DeleteKey()
    {
        var keyStore = KeyStore.GetInstance(KeyStoreName)!;
        keyStore.Load(null);
        if (keyStore.ContainsAlias(KeyAlias)) keyStore.DeleteEntry(KeyAlias);
    }

    /// <summary>两套 padding 名字都试一遍；都不支持才抛（消息里带上第一个的原因）。</summary>
    private static Cipher NewCipher()
    {
        Exception? firstError = null;
        foreach (var transformation in CbcTransformations)
        {
            try
            {
                return Cipher.GetInstance(transformation)!;
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }
        }

        throw new TamaException(
            $"系统不支持 AES/CBC（{string.Join(" 与 ", CbcTransformations)} 都拿不到）：{Describe(firstError)}",
            firstError!);   // 循环里至少失败过两次才会走到这里
    }

    private static string Describe(Exception? ex)
        => ex == null ? "未知错误" : $"{ex.GetType().Name}: {ex.Message}";
}
