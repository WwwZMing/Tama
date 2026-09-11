using Tama.Core;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Tama.Core.Interfaces;

namespace Tama.Platforms.Windows;

/// <summary>
/// DPAPI（crypt32.dll）P/Invoke：net11.0-windows10.0.19041.0（WinRT TFM）不含
/// System.Security.Cryptography.ProtectedData，为避免引包直接调 CryptProtectData。
/// CurrentUser 范围 = 只有同一 Windows 用户能解出密钥。
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [Flags]
    private enum CryptProtectFlags { None = 0 }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, CryptProtectFlags dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, CryptProtectFlags dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = ptr };
    }

    private static byte[] FromBlob(DATA_BLOB blob)
    {
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        LocalFree(blob.pbData);
        return result;
    }

    public static byte[] Protect(byte[] data)
    {
        var input = ToBlob(data);
        try
        {
            if (!CryptProtectData(ref input, "Tama biometric key", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectFlags.None, out var output))
                throw Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error())!;
            return FromBlob(output);
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    public static byte[] Unprotect(byte[] data)
    {
        var input = ToBlob(data);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectFlags.None, out var output))
                throw Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error())!;
            return FromBlob(output);
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }
}

/// <summary>
/// Windows 生物识别解锁实现（对齐 Android 的 AndroidKeyStore 方案）：
/// - AES-256 密钥用 DPAPI（CurrentUser）保护后落盘 biometric.key——解密必须有本机当前用户上下文；
/// - 解密门禁 = Windows Hello 验证窗（UserConsentVerifier），验证不过拿不到主密码；
/// - 密文格式与 Android 一致："iv.ct"（Base64(iv).Base64(密文)），GCM tag 追加在密文尾部；
/// - 已保存的加密密码落盘 biometric.dat（等价 Android 的 prefs）。
/// </summary>
public class WindowsBiometricService : IBiometricService
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

    /// <summary>解锁门禁：先弹 Windows Hello 验证，通过才算可用（与 Android BiometricPrompt 语义一致）</summary>
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
