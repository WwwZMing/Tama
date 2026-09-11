using Tama.Core;

namespace Tama.Tests;

/// <summary>
/// AppPaths 旧数据迁移（Keyguard → Tama，2026-09-11 改名）。
///
/// 为什么专门测这段：它动的是用户**唯一的**保险库目录，而且只在"第一次以新名字启动"时
/// 跑一次——跑错了没有任何第二次机会。已实测踩到两个真 bug，都由这里锁住：
///   1. 某组件抢在迁移前创建了新数据目录（日志初始化先建 logs/），迁移于是判定
///      "两边都在"而撒手 → 用户的库永远搬不过来；
///   2. 迁移只搬目录、**没改库文件名**（keyguard.db → tama.db）→ 应用去开一个不存在的
///      新库，用户看到"要重新 Setup"，而真库还躺在旧名字下。
/// </summary>
public class AppPathsMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tama-apppaths-" + Guid.NewGuid().ToString("N"));

    public AppPathsMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录清理失败不影响结论 */ }
        GC.SuppressFinalize(this);
    }

    private string LegacyDir => Path.Combine(_root, "Keyguard");
    private string CurrentDir => Path.Combine(_root, "Tama");

    /// <summary>造一个"像真的"旧数据目录：两个库 + salt + 指纹凭据 + 日志子目录。</summary>
    private void SeedLegacy()
    {
        Directory.CreateDirectory(LegacyDir);
        File.WriteAllText(Path.Combine(LegacyDir, "keyguard.db"), "SQLite format 3\0fake-vault");
        File.WriteAllText(Path.Combine(LegacyDir, "keyguard-auth.db"), "fake-auth");
        File.WriteAllText(Path.Combine(LegacyDir, "keyguard.db-wal"), "wal");
        File.WriteAllText(Path.Combine(LegacyDir, "fingerprint.json"), """{"salt":"abc","kdfIterations":100000}""");
        File.WriteAllText(Path.Combine(LegacyDir, "biometric.key"), "dpapi-key");
        Directory.CreateDirectory(Path.Combine(LegacyDir, "logs"));
        File.WriteAllText(Path.Combine(LegacyDir, "logs", "keyguard-20260911.log"), "log");
    }

    [Fact]
    public void NoLegacyVault_IsNotNeeded_AndCreatesNothing()
    {
        // 旧目录只有日志、没有库 → 没有要搬的东西
        Directory.CreateDirectory(LegacyDir);
        File.WriteAllText(Path.Combine(LegacyDir, "readme.txt"), "nothing important");

        var result = AppPaths.MigrateLegacyDataDirIfNeeded(_root);

        Assert.Equal(AppPaths.Migration.NotNeeded, result);
        Assert.False(Directory.Exists(CurrentDir));
        Assert.True(Directory.Exists(LegacyDir));
    }

    [Fact]
    public void LegacyOnly_MigratesAndRenamesDbFiles()
    {
        SeedLegacy();

        var result = AppPaths.MigrateLegacyDataDirIfNeeded(_root);

        Assert.Equal(AppPaths.Migration.Migrated, result);
        Assert.False(Directory.Exists(LegacyDir));   // 旧目录整体让位
        Assert.True(Directory.Exists(CurrentDir));

        // 库文件必须**改名**——否则应用会去开一个不存在的 tama.db，用户看到"要重新 Setup"
        Assert.Equal("SQLite format 3\0fake-vault", File.ReadAllText(Path.Combine(CurrentDir, "tama.db")));
        Assert.Equal("fake-auth", File.ReadAllText(Path.Combine(CurrentDir, "tama-auth.db")));
        Assert.Equal("wal", File.ReadAllText(Path.Combine(CurrentDir, "tama.db-wal")));   // 边车文件跟着改名
        Assert.False(File.Exists(Path.Combine(CurrentDir, "keyguard.db")));
        Assert.False(File.Exists(Path.Combine(CurrentDir, "keyguard-auth.db")));

        // 库以外的数据也要在（少搬任何一个都会表现为"密码对了但解不开库 / 指纹按钮消失"）
        Assert.Equal("""{"salt":"abc","kdfIterations":100000}""",
            File.ReadAllText(Path.Combine(CurrentDir, "fingerprint.json")));
        Assert.Equal("dpapi-key", File.ReadAllText(Path.Combine(CurrentDir, "biometric.key")));
        Assert.Equal("log", File.ReadAllText(Path.Combine(CurrentDir, "logs", "keyguard-20260911.log")));
    }

    [Fact]
    public void CurrentDirWithOnlyLogs_IsSetAsideAndLegacyStillMigrates()
    {
        // 实测场景：LogSetup.CreateLogger() 抢在迁移之前建出了 logs/，
        // 老逻辑看到"新位置已存在且非空"就撒手 → 库永远搬不过来。
        SeedLegacy();
        Directory.CreateDirectory(Path.Combine(CurrentDir, "logs"));
        File.WriteAllText(Path.Combine(CurrentDir, "logs", "tama-20260911.log"), "early log");

        var result = AppPaths.MigrateLegacyDataDirIfNeeded(_root);

        Assert.Equal(AppPaths.Migration.Migrated, result);
        Assert.Equal("SQLite format 3\0fake-vault", File.ReadAllText(Path.Combine(CurrentDir, "tama.db")));
        Assert.Equal("log", File.ReadAllText(Path.Combine(CurrentDir, "logs", "keyguard-20260911.log")));

        // 壳目录不删、挪到一边留档（不丢任何东西）
        var setAside = Directory.GetDirectories(_root, "Tama.pre-migration-*");
        Assert.Single(setAside);
        Assert.Equal("early log", File.ReadAllText(Path.Combine(setAside[0], "logs", "tama-20260911.log")));
    }

    [Fact]
    public void CurrentDirAlreadyHasVault_LeavesBothUntouched()
    {
        SeedLegacy();
        Directory.CreateDirectory(CurrentDir);
        File.WriteAllText(Path.Combine(CurrentDir, "tama.db"), "already-migrated");

        var result = AppPaths.MigrateLegacyDataDirIfNeeded(_root);

        Assert.Equal(AppPaths.Migration.BothExist, result);
        // 不许"帮忙合并"：新位置已有真库时任何自动动作都可能覆盖用户数据，只报告不动手
        Assert.True(Directory.Exists(LegacyDir));
        Assert.Equal("SQLite format 3\0fake-vault", File.ReadAllText(Path.Combine(LegacyDir, "keyguard.db")));
        Assert.Equal("already-migrated", File.ReadAllText(Path.Combine(CurrentDir, "tama.db")));
    }

    [Fact]
    public void SecondCallAfterMigration_IsNoOp()
    {
        SeedLegacy();
        Assert.Equal(AppPaths.Migration.Migrated, AppPaths.MigrateLegacyDataDirIfNeeded(_root));

        // 幂等：宿主每次启动都会调，不能第二次报错或反复搬
        Assert.Equal(AppPaths.Migration.NotNeeded, AppPaths.MigrateLegacyDataDirIfNeeded(_root));
        Assert.Equal("SQLite format 3\0fake-vault", File.ReadAllText(Path.Combine(CurrentDir, "tama.db")));
    }

    [Fact]
    public void FileNames_AreDerivedFromDataDir_NotDuplicated()
    {
        // 单一来源的意义：这些名字只该由 AppPaths 拼一次，改了目录名它们必须跟着变。
        // 注意：本测试**不得**调用 AppPaths.Initialize() —— 那会去动真实的
        // %LOCALAPPDATA%（第一版就犯过这个错：测试跑完把开发机的数据目录搬了）。
        Assert.StartsWith(AppPaths.DataDir, AppPaths.VaultDbPath);
        Assert.StartsWith(AppPaths.DataDir, AppPaths.AuthDbPath);
        Assert.StartsWith(AppPaths.DataDir, AppPaths.FingerprintPath);
        Assert.StartsWith(AppPaths.DataDir, AppPaths.SessionPath);
        Assert.StartsWith(AppPaths.DataDir, AppPaths.ThemeModePath);
        Assert.StartsWith(AppPaths.DataDir, AppPaths.BridgePortPath);
        Assert.StartsWith(AppPaths.DataDir, AppPaths.LogDir);
        Assert.StartsWith(AppPaths.DataDir, AppPaths.DataProtectionKeysDir);

        Assert.DoesNotContain("Keyguard", AppPaths.DataDir, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("tama.db", AppPaths.VaultDbPath, StringComparison.Ordinal);
        Assert.EndsWith("tama-auth.db", AppPaths.AuthDbPath, StringComparison.Ordinal);
    }
}
