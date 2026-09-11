namespace Tama.Core;

/// <summary>
/// 应用数据目录与文件名的**唯一来源**（2026-09-11 收口）。
///
/// 为什么放在 Core：这里是唯一"零依赖、且每个可执行宿主都引用得到"的程序集。
/// 路径拼接曾经散在 6 个文件里各写一遍（两个宿主 + NativeHost + LogSetup +
/// AuthSessionService + MainPage），改一次目录名要满仓找——现在只有这一处。
///
/// 为什么不放在 Services：NativeHost 是独立发布的极小可执行文件，刻意不引业务层；
/// 让它为了一个路径常量拖进 EF/Serilog 是反向依赖。
/// </summary>
public static class AppPaths
{
    /// <summary>产品名。同时是 %LOCALAPPDATA% 下的数据目录名。</summary>
    public const string ProductName = "Tama";

    /// <summary>改名前的数据目录名（2026-09-11 由 Keyguard 改为 Tama）。只为一次性迁移存在。</summary>
    private const string LegacyProductName = "Keyguard";

    private const string VaultDbName = "tama.db";
    private const string AuthDbName = "tama-auth.db";
    private const string LegacyVaultDbName = "keyguard.db";
    private const string LegacyAuthDbName = "keyguard-auth.db";

    private static string LocalAppData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>数据根目录：%LOCALAPPDATA%\Tama\</summary>
    public static string DataDir { get; } = Path.Combine(LocalAppData, ProductName);

    /// <summary>旧数据根目录：%LOCALAPPDATA%\Keyguard\。迁移后不应再被业务代码引用。</summary>
    public static string LegacyDataDir { get; } = Path.Combine(LocalAppData, LegacyProductName);

    public static string VaultDbPath => Path.Combine(DataDir, VaultDbName);
    public static string AuthDbPath => Path.Combine(DataDir, AuthDbName);
    public static string FingerprintPath => Path.Combine(DataDir, "fingerprint.json");
    public static string SessionPath => Path.Combine(DataDir, "session.json");
    public static string ThemeModePath => Path.Combine(DataDir, "theme-mode.txt");
    public static string BridgePortPath => Path.Combine(DataDir, "bridge.port");

    /// <summary>
    /// Bitwarden 的 <c>deviceIdentifier</c>。**必须跨次登录保持稳定**：
    /// "新设备邮箱验证"是按设备发码的，每次登录都换一个 GUID 的话，
    /// 第一步（无码）触发的验证码属于设备 A，第二步带着码回来的是设备 B → 码永远对不上。
    /// 文件不存在时由客户端生成一次并落盘。
    /// </summary>
    public static string DeviceIdPath => Path.Combine(DataDir, "device.id");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string DataProtectionKeysDir => Path.Combine(DataDir, "dpkeys");

    /// <summary>迁移结果，供宿主记日志（Core 不依赖 Serilog，不在这里打日志）。</summary>
    public enum Migration
    {
        /// <summary>旧位置没有保险库——没有要搬的东西（旧目录里可能只剩日志）。</summary>
        NotNeeded,
        /// <summary>目录与库文件名都已就位，数据无损。</summary>
        Migrated,
        /// <summary>新位置已经有保险库（用户已迁移过，或自己在新位置建过库）——两边都不动。</summary>
        BothExist,
        /// <summary>失败：**什么都没动**（旧位置保持原样），数据没丢但需要人工处理。</summary>
        Failed,
    }

    /// <summary>
    /// 把改名前的数据整体搬到新位置（一次性、幂等、可重复调用）。
    ///
    /// 为什么是"整体搬目录"而不是逐文件搬运：目录里不止数据库——还有
    /// fingerprint.json（PBKDF2 salt）、biometric.key/.dat（DPAPI 包的主密码）、
    /// session.json（72h 重验证）、bridge.port、theme-mode.txt、dpkeys/、logs/。
    /// 漏搬任何一个都会表现为"密码对了但解不开库"或"指纹按钮消失"这类灵异现象。
    ///
    /// **必须跟着改名的不只是目录**：库文件名也从 keyguard.db / keyguard-auth.db 改成了
    /// tama.db / tama-auth.db。只搬目录不改名的话，应用会去开一个不存在的新库 →
    /// 用户看到"要重新 Setup"，而真库还躺在旧文件名下（实测差点上线这个 bug）。
    ///
    /// 语义保证：要么全部就位（<see cref="Migration.Migrated"/>），要么**原地不动**
    /// （<see cref="Migration.Failed"/> 会回滚已做的动作），不会留下半搬状态。
    /// </summary>
    public static Migration MigrateLegacyDataDirIfNeeded() => MigrateLegacyDataDirIfNeeded(LocalAppData);

    /// <summary>
    /// 上面的可测版本：根目录由调用方给（单测传临时目录）。
    /// 这段逻辑动的是用户唯一的保险库，必须能被测试覆盖——不能只靠"跑一次看着没问题"。
    /// </summary>
    public static Migration MigrateLegacyDataDirIfNeeded(string localAppDataRoot)
    {
        var legacy = Path.Combine(localAppDataRoot, LegacyProductName);
        var current = Path.Combine(localAppDataRoot, ProductName);
        string? setAside = null;

        try
        {
            // 判定"有没有保险库"看的是库文件或指纹盐，而不是"目录存在与否"——
            // 数据目录在启动早期就会被日志/端口文件自动创建出来，按目录判会误判。
            if (!HasVault(legacy, LegacyVaultDbName)) return Migration.NotNeeded;

            // 新位置已经有库 → 用户已经迁过，或自己在新位置建了库。绝不覆盖。
            if (HasVault(current, VaultDbName)) return Migration.BothExist;

            if (Directory.Exists(current))
            {
                // 新位置存在但**没有库**：只可能是启动早期自动生成的壳（logs/、bridge.port）。
                // 不删——挪到一边留档，再把旧目录整体搬进来。这样即使某个组件抢在迁移之前
                // 碰了数据目录，用户的库也不会被永远晾在旧位置（实测踩过：LogSetup 先建了 logs/）。
                setAside = $"{current}.pre-migration-{DateTime.Now:yyyyMMdd-HHmmss}";
                Directory.Move(current, setAside);
            }

            Directory.Move(legacy, current);

            try
            {
                // 库文件改名（含 SQLite 的 -wal / -shm 边车文件：名字不跟着改就成孤儿）
                RenameDbFiles(current, LegacyVaultDbName, VaultDbName);
                RenameDbFiles(current, LegacyAuthDbName, AuthDbName);
            }
            catch
            {
                // 回滚，保持"要么全成、要么全不动"
                Directory.Move(current, legacy);
                if (setAside is not null) Directory.Move(setAside, current);
                return Migration.Failed;
            }

            return Migration.Migrated;
        }
        catch
        {
            // 目录搬运本身失败：旧位置未被触碰（setAside 若已发生则原样留在磁盘上，不丢东西）
            return Migration.Failed;
        }
    }

    /// <summary>宿主启动时调用一次：完成旧数据迁移 + 确保数据目录存在。
    /// **必须是宿主启动后的第一件事**（早于日志初始化等任何会创建目录的动作）。</summary>
    public static Migration Initialize()
    {
        var migration = MigrateLegacyDataDirIfNeeded();
        Directory.CreateDirectory(DataDir);
        return migration;
    }

    private static bool HasVault(string dir, string vaultDbName) =>
        File.Exists(Path.Combine(dir, vaultDbName)) || File.Exists(Path.Combine(dir, "fingerprint.json"));

    private static void RenameDbFiles(string dir, string from, string to)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var src = Path.Combine(dir, from + suffix);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(dir, to + suffix);
            if (File.Exists(dst)) continue;   // 目标已存在就不动它（宁可留个孤儿文件，也不覆盖数据）
            File.Move(src, dst);
        }
    }
}
