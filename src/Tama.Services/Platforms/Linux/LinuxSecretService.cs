using Serilog;

namespace Tama.Services.Platforms.Linux;

/// <summary>
/// 用 <c>secret-tool</c>（libsecret → Secret Service，GNOME Keyring / KWallet 都实现它）
/// 保管指纹解锁用的 AES 密钥 —— 这在 Linux 上对应 Windows 的 DPAPI。
///
/// 为什么**不做落盘兜底**：密钥和它保护的密文（biometric.dat）在同一台机器上，
/// 密钥一旦明文落盘，拿到磁盘的人不需要任何指纹就能解出主密码 ——
/// 那样指纹就只是个障眼法。所以拿不到密钥环时宁可让这个功能整体不可用
/// （IsAvailable 返回 false），也不静默降级成明文。与 NullBiometricService 的
/// "绝不回退为明文" 是同一条原则。
///
/// 需要**会话**总线（不是系统总线）：secret-tool 通过 DBUS_SESSION_BUS_ADDRESS 找密钥环。
/// </summary>
internal static class LinuxSecretService
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(LinuxSecretService));

    private const string Label = "Tama · 指纹解锁密钥";

    /// <summary>属性键值对：service=tama, purpose=biometric-key。</summary>
    private static readonly string[] Attributes = { "service", "tama", "purpose", "biometric-key" };

    /// <summary>
    /// 只看"能不能用"的轻量判据：有 secret-tool + 有会话总线。
    /// 刻意不在这里做真写入探测 —— 本方法会被页面渲染路径调用，
    /// 每次去密钥环里写删一个探针项既慢又唐突。真失败时 Store/Lookup 会返回
    /// false/null，上层（Unlock 页）表现为"存不上 → 不显示指纹按钮"，自然降级。
    /// </summary>
    public static bool IsAvailable()
        => OperatingSystem.IsLinux()
           && LinuxShell.Exists("secret-tool")
           && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));

    /// <summary>把密钥存进密钥环（覆盖旧值）。失败返回 false。</summary>
    public static bool Store(byte[] key)
    {
        var encoded = Convert.ToBase64String(key);

        var args = new List<string> { "store", $"--label={Label}" };
        args.AddRange(Attributes);

        if (!LinuxShell.TryRun("secret-tool", args.ToArray(), out var result,
                stdin: encoded, timeout: TimeSpan.FromSeconds(10)) || !result.Ok)
        {
            Log.Error("密钥环写入失败：{Err}", result.StdErr);
            return false;
        }
        return true;
    }

    /// <summary>读回密钥；不存在或密钥环不可用返回 null。</summary>
    public static byte[]? Lookup()
    {
        var args = new List<string> { "lookup" };
        args.AddRange(Attributes);

        if (!LinuxShell.TryRun("secret-tool", args.ToArray(), out var result,
                timeout: TimeSpan.FromSeconds(10)) || !result.Ok)
        {
            return null;
        }

        // secret-tool 会在密文后补一个换行，base64 解码前必须去掉
        var text = result.StdOut.Trim();
        if (text.Length == 0) return null;

        try
        {
            var key = Convert.FromBase64String(text);
            return key.Length == 32 ? key : null;
        }
        catch (FormatException ex)
        {
            Log.Error(ex, "密钥环里的密钥不是合法 base64");
            return null;
        }
    }

    /// <summary>从密钥环删除密钥（关闭指纹解锁时调用）。</summary>
    public static void Clear()
    {
        var args = new List<string> { "clear" };
        args.AddRange(Attributes);

        // 删不掉不算错误（本来就没有），但记一笔便于排查
        if (!LinuxShell.TryRun("secret-tool", args.ToArray(), out var result, timeout: TimeSpan.FromSeconds(10))
            || !result.Ok)
        {
            Log.Debug("密钥环清除未成功（可能本就没有）：{Err}", result.StdErr);
        }
    }
}
