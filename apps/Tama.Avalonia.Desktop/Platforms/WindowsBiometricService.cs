using System.Security.Cryptography;
using System.Text;
using Tama.Core;
using Tama.Core.Interfaces;

namespace Tama.Avalonia.Desktop.Platforms;

/// <summary>
/// Windows 生物识别解锁实现（与 Android 的 AndroidKeyStore 方案语义一致）：
///   · AES-256 密钥用 DPAPI（CurrentUser）保护后落盘 <c>biometric.key</c>——解密需要本机当前用户上下文；
///   · 解密门禁 = Windows Hello 验证窗，验证不过拿不到主密码；
///   · 密文格式与 Android 一致：<c>"iv.ct"</c>（Base64(iv).Base64(密文)），GCM tag 追加在密文尾部；
///   · 已保存的加密主密码落 <c>biometric.dat</c>（等价 Android 的 prefs）。
/// </summary>
public sealed class WindowsBiometricService : IBiometricService
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<WindowsBiometricService>();

    private const string KeyFileName = "biometric.key";
    private const string DataFileName = "biometric.dat";

    private static string DataDir => AppPaths.DataDir;
    private static string KeyFilePath => Path.Combine(DataDir, KeyFileName);
    private static string DataFilePath => Path.Combine(DataDir, DataFileName);

    public bool IsAvailable() => WindowsHelloPrompt.IsAvailable();

    public bool IsConfigured() => File.Exists(DataFilePath);

    public void SetupKey()
    {
        if (File.Exists(KeyFilePath)) return;

        Directory.CreateDirectory(DataDir);
        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = Dpapi.Protect(key);
        File.WriteAllBytes(KeyFilePath, protectedKey);
        CryptographicOperations.ZeroMemory(key);
        Log.Information("Biometric AES key created (DPAPI-protected)");
    }

    public string? EncryptPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        var key = LoadKey();
        if (key == null) return null; // 密钥缺失绝不回退明文

        try
        {
            var iv = RandomNumberGenerator.GetBytes(12);
            var plain = Encoding.UTF8.GetBytes(password);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(iv, plain, cipher, tag);

            // tag 追加在密文尾部，整体仍是 "iv.ct" 形态
            var ct = new byte[cipher.Length + tag.Length];
            Buffer.BlockCopy(cipher, 0, ct, 0, cipher.Length);
            Buffer.BlockCopy(tag, 0, ct, cipher.Length, tag.Length);

            return $"{Convert.ToBase64String(iv)}.{Convert.ToBase64String(ct)}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Biometric password encrypt failed");
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string? DecryptPassword(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        var key = LoadKey();
        if (key == null) return null;

        try
        {
            var parts = encrypted.Split('.');
            if (parts.Length != 2) return null;

            var iv = Convert.FromBase64String(parts[0]);
            var ct = Convert.FromBase64String(parts[1]);
            if (ct.Length < 16) return null;

            var cipher = ct.AsSpan(0, ct.Length - 16);
            var tag = ct.AsSpan(ct.Length - 16);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(iv, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Biometric password decrypt failed");
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void SaveEncryptedPassword(string encrypted)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(DataFilePath, encrypted);
    }

    public string? GetEncryptedPassword() =>
        File.Exists(DataFilePath) ? File.ReadAllText(DataFilePath) : null;

    public void ClearEncryptedPassword()
    {
        if (File.Exists(DataFilePath)) File.Delete(DataFilePath);
    }

    public Task<bool> AuthenticateAsync(string title, string subtitle) =>
        WindowsHelloPrompt.AuthenticateAsync(title, subtitle);

    private static byte[]? LoadKey()
    {
        try
        {
            if (!File.Exists(KeyFilePath))
            {
                Log.Warning("Biometric key file missing - call SetupKey first");
                return null;
            }
            return Dpapi.Unprotect(File.ReadAllBytes(KeyFilePath));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Biometric key load failed (DPAPI unprotect)");
            return null;
        }
    }
}
