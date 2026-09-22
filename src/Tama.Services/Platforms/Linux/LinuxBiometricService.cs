using System.Security.Cryptography;
using System.Text;
using Tama.Core;
using Tama.Core.Interfaces;
using Serilog;

namespace Tama.Services.Platforms.Linux;

/// <summary>
/// Linux 指纹解锁实现，对齐 Windows 的 <c>WindowsBiometricService</c>（DPAPI + Windows Hello）：
///   · AES-256 密钥交给<b>登录密钥环</b>保管（secret-tool / Secret Service）——对应 Windows 的 DPAPI；
///   · 解密门禁 = <b>fprintd 指纹验证</b>——对应 Windows Hello 验证窗；
///   · 密文格式与 Windows / Android 完全一致："iv.ct"（Base64(iv).Base64(密文 + GCM tag)），
///     所以这三个平台保存的凭据可以互读，UI 与 AuthService 一行都不用改；
///   · 已保存的密文落盘 biometric.dat（与 Windows 同名同义）。
///
/// 诚实说明（与 Windows 实现同等强度，不多也不少）：
/// 指纹门禁是**应用层**的，不是密码学门禁 —— 能读到你登录密钥环的进程同样能拿到 AES 密钥
/// 并解开 biometric.dat。Windows 那边也一样（同用户的任何进程都能 DPAPI 解出 biometric.key）。
/// 它真正防的是"人离开座位时有人顺手点开"，以及"磁盘被拿走"（密钥环文件由登录口令加密）。
/// </summary>
public class LinuxBiometricService : IBiometricService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LinuxBiometricService>();

    private const string DataFileName = "biometric.dat";

    /// <summary>
    /// 等用户按手指的上限。Windows Hello 那扇窗是无限等的，这里必须有界：
    /// 用户走开后 UI 不该永远停在"正在验证…"，fprintd 的设备也要及时释放给别人用。
    /// </summary>
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(30);

    private static string DataDir => AppPaths.DataDir;
    private static string DataFilePath => Path.Combine(DataDir, DataFileName);

    /// <summary>两个条件都要：有指纹硬件且录过手指，<b>并且</b>有密钥环可安全保管密钥。</summary>
    public bool IsAvailable() => LinuxFprintd.IsAvailable() && LinuxSecretService.IsAvailable();

    public bool IsConfigured() => File.Exists(DataFilePath);

    public void SetupKey()
    {
        // 已经有密钥就复用（重复调用是常态：每次用主密码解锁成功后都会走一遍）
        if (LinuxSecretService.Lookup() is not null) return;

        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            if (LinuxSecretService.Store(key))
                Log.Information("指纹解锁的 AES 密钥已存入登录密钥环");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string? EncryptPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        var key = LinuxSecretService.Lookup();
        if (key is null) return null;      // 密钥缺失绝不回退明文（与 Windows/Android 同一条原则）

        try
        {
            var iv = RandomNumberGenerator.GetBytes(12);
            var plain = Encoding.UTF8.GetBytes(password);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(iv, plain, cipher, tag);

            // tag 追加在密文尾部，整体仍是 "iv.ct" 形态（与 Windows 逐字节同构）
            var ct = new byte[cipher.Length + tag.Length];
            Buffer.BlockCopy(cipher, 0, ct, 0, cipher.Length);
            Buffer.BlockCopy(tag, 0, ct, cipher.Length, tag.Length);

            return $"{Convert.ToBase64String(iv)}.{Convert.ToBase64String(ct)}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "指纹凭据加密失败");
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
        var key = LinuxSecretService.Lookup();
        if (key is null) return null;

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
            Log.Warning(ex, "指纹凭据解密失败");
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

        // 0600：密文本身解不开，但没必要让同机其他用户读到
        if (OperatingSystem.IsLinux())
        {
            try { File.SetUnixFileMode(DataFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception ex) { Log.Debug(ex, "设置 biometric.dat 权限失败"); }
        }
    }

    public string? GetEncryptedPassword() =>
        File.Exists(DataFilePath) ? File.ReadAllText(DataFilePath) : null;

    /// <summary>
    /// 清除凭据。与 Windows 实现口径一致：只删密文，不动密钥环里的密钥
    /// （单独一把用不上的密钥不构成凭据，留着下次启用时直接复用）。
    /// </summary>
    public void ClearEncryptedPassword()
    {
        if (File.Exists(DataFilePath)) File.Delete(DataFilePath);
    }

    /// <summary>解锁门禁：fprintd 验证通过才算数（与 Windows Hello / Android BiometricPrompt 语义一致）。</summary>
    public Task<bool> AuthenticateAsync(string title, string subtitle) =>
        LinuxFprintd.VerifyAsync(VerifyTimeout);
}

/// <summary>
/// Linux 的 Passkey 平台确认：通行密钥的创建/登录由 <c>PasskeyService</c> / <c>WebAuthnService</c>
/// 处理（那部分本来就跨平台），这里只提供"用户在场证明"这一件事 —— 同样用 fprintd。
///
/// 所以「指纹解锁」做完，通行密钥在 Linux 上就顺带成立了：两者要的是同一个原语。
/// 判据比生物识别松一档：通行密钥不依赖登录密钥环，只要有指纹设备就够了。
/// </summary>
public class LinuxPasskeyPlatformService : IPasskeyPlatformService
{
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(30);

    public bool IsAvailable() => LinuxFprintd.IsAvailable();

    public Task<bool> AuthenticateAsync(string title, string subtitle) =>
        LinuxFprintd.VerifyAsync(VerifyTimeout);
}
