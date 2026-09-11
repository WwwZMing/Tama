using Tama.Core;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;
using Tama.Data.Database;
using Tama.Services.Auth;
using Tama.Services.Bitwarden;
using Tama.Services.Bridge;
using Tama.Services.Import;
using Tama.Services.Passkey;
using Tama.Services.Passwords;
using Tama.Services.Sync;
using Tama.Services.Vault;
using Tama.Services.Watchtower;
using Tama.Services.WebAuthn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Tama.Services;

/// <summary>
/// 领域服务的 DI 注册——三个可执行宿主（MAUI / Blazor Server / NativeHost 不需要）
/// 共用这一份，避免同 30 行在两处各写一遍然后慢慢走样。
///
/// 宿主自己负责的部分：
///   • 平台能力：<see cref="IBiometricService"/>、<see cref="IPasskeyPlatformService"/>（Windows Hello / Android / Null 降级）
///   • 后台 Worker（<c>SyncWorker</c>）与宿主专属服务（<c>LocalBridgeServer</c>、<c>ShellLoading</c>、IHostIntegration）
///     —— ⚠ 两个 UI 宿主（MAUI 与 Tama.Api）**都必须** <c>AddHostedService&lt;SyncWorker&gt;()</c>：
///     它是离线写入队列唯一的消费者，漏注册的那个宿主里，离线改的东西永远推不上去
///   • 数据目录初始化：启动时先调 <see cref="AppPaths.Initialize"/>（内含旧目录迁移）
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTamaServices(this IServiceCollection services)
    {
        // === 会话与密钥（纯内存；密钥由 AuthSessionService 在验证主密码后写入）===
        services.AddSingleton<DatabaseKeyService>();
        services.AddSingleton<DatabaseProtectionService>();
        services.AddSingleton<IVaultCrypto, VaultCryptoService>();
        services.AddSingleton<IAuthSession, AuthSessionService>();
        services.AddSingleton<IBitwardenApiClient>(new BitwardenApiClient());
        // 后台同步完成的通知面：发布方在 Services、订阅方在 UI，本身无状态，刻意 Singleton
        // （架构规则 7 的"服务是 Scoped"针对领域服务；这里是个事件汇总点）
        services.AddSingleton<IVaultSyncNotifier, VaultSyncNotifier>();
        // 全量拉取的进程级闸门：解锁同步 / 账号页手动同步 / 定时轮询可能落在不同 scope，必须共用一把
        services.AddSingleton<VaultSyncGate>();

        // === 领域服务（具体类型；部分服务之间互相注入）===
        services.AddScoped<BitwardenAccountService>();
        services.AddScoped<AuthService>();
        services.AddScoped<CipherService>();
        services.AddScoped<FolderService>();
        services.AddScoped<WatchtowerService>();
        services.AddScoped<PasswordService>();
        services.AddScoped<PasskeyService>();
        services.AddScoped<WebAuthnService>();
        services.AddScoped<KeePassImportService>();
        // 离线写入队列的消费者（条目 + 文件夹）。由宿主的 SyncWorker 每轮调一次，
        // 抽成 Scoped 服务是为了能被单测直接驱动（BackgroundService 的 while 循环没法测）。
        services.AddScoped<PendingSyncProcessor>();

        // === 领域契约接口 → 上面的具体服务（同一实例）===
        // 页面只注入接口，于是 Tama.UI 不需要引用 Tama.Services。
        services.AddScoped<IAuthApi>(sp => sp.GetRequiredService<AuthService>());
        services.AddScoped<IVaultApi>(sp => sp.GetRequiredService<CipherService>());
        services.AddScoped<IFolderApi>(sp => sp.GetRequiredService<FolderService>());
        services.AddScoped<IWatchtowerApi>(sp => sp.GetRequiredService<WatchtowerService>());
        services.AddScoped<IPasswordApi>(sp => sp.GetRequiredService<PasswordService>());
        services.AddScoped<IPasskeyApi>(sp => sp.GetRequiredService<PasskeyService>());
        services.AddScoped<IWebAuthnApi>(sp => sp.GetRequiredService<WebAuthnService>());
        services.AddScoped<IImportApi>(sp => sp.GetRequiredService<KeePassImportService>());

        // === 扩展 native-host 的 JSON 边界（唯一字符串 JSON 入口）===
        services.AddScoped<ExtensionBridge>();

        return services;
    }

    /// <summary>
    /// 注册两个加密 SQLite 库（保险库 / 账号）。
    ///
    /// 抽出来的理由：两个宿主原本各写一遍同样的 20 行，连"连接串不带 Password"那段长注释
    /// 都是复制粘贴——同一件事维护两份必然走样（Api 少了 DEBUG 段、MAUI 的库名硬编码）。
    ///
    /// **别改回去的约束**：连接串不带 Password。DbContextOptions 是 Singleton，密码若烤死在
    /// 工厂里，会在服务图构造时（解锁前）就执行 GetDbPassword() 并抛 "Vault is locked"，
    /// 阻断所有调用。密码由 <see cref="VaultDbConnectionInterceptor"/> 在连接打开时注入当前密钥。
    /// </summary>
    /// <param name="sqlLog">DEBUG 下把 EF 生成的 SQL 转出去（传 null 表示不记录）。</param>
    /// <param name="sensitiveDataLogging">
    /// 是否开启 EF 敏感数据日志（会记录参数值）。只有桌面开发宿主开；浏览器宿主刻意不开——
    /// 密码管理器里把派生值写进日志不是"顺手一起开"的事。
    /// </param>
    public static IServiceCollection AddTamaDataStores(this IServiceCollection services,
        Action<string>? sqlLog = null, bool sensitiveDataLogging = false)
    {
        services.AddDbContext<TamaDbContext>((sp, options) =>
            Configure(options, sp, AppPaths.VaultDbPath, sqlLog, sensitiveDataLogging));

        services.AddDbContext<AuthDbContext>((sp, options) =>
            Configure(options, sp, AppPaths.AuthDbPath, sqlLog, sensitiveDataLogging));

        return services;
    }

    private static void Configure(DbContextOptionsBuilder options, IServiceProvider sp, string dbPath,
        Action<string>? sqlLog, bool sensitiveDataLogging)
    {
        options.UseSqlite($"Data Source={dbPath}")
               .AddInterceptors(new VaultDbConnectionInterceptor(sp.GetRequiredService<DatabaseKeyService>()));

#if DEBUG
        if (sensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
        }
        if (sqlLog is not null) options.LogTo(sqlLog, Microsoft.Extensions.Logging.LogLevel.Information);
#else
        _ = sqlLog;
        _ = sensitiveDataLogging;
#endif
    }
}
