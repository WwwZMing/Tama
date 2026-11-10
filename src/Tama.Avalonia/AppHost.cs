using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Tama.Avalonia.Services;
using Tama.Avalonia.ViewModels;
using Tama.Core;
using Tama.Services;
using Tama.Services.Logging;
using Tama.Services.Sync;

namespace Tama.Avalonia;

/// <summary>
/// 一次进程运行的全部非 UI 环境：通用主机（DI + 后台 Worker）与 UI 会话（DI 作用域）。
///
/// 为什么 UI 自己管作用域：领域服务是 Scoped（DbContext 挂在上面），而 Avalonia 应用是**一个
/// 长活进程**——从根容器直接取 Scoped 必然共享同一个 DbContext 且永远释放不掉。
/// 这里给"一个 UI 会话"配一个作用域，锁定/解锁时重建，语义与 Blazor 的"一个电路一个作用域"
/// 相同（也是既有宿主的行为，不是新发明的规矩）。
/// </summary>
public sealed class AppEnvironment : IDisposable
{
    public required IHost Host { get; init; }
    public required AppSession Session { get; init; }

    public void Dispose()
    {
        try { Host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); } catch { /* 退出路径不抛 */ }
        Session.Dispose();
        Host.Dispose();
        Log.CloseAndFlush();
    }
}

public static class AppHost
{
    /// <summary>
    /// 建主机。**平台头必须只调这一个方法**，然后自己再加平台服务（生物识别等）。
    /// </summary>
    /// <param name="configurePlatform">
    /// 平台头的额外注册（<c>IBiometricService</c> / <c>IPasskeyPlatformService</c> 的真机实现）。
    /// 不给也行——<see cref="AddAvaloniaUi"/> 里有 Null 降级兜底。
    /// </param>
    public static AppEnvironment Build(Action<IServiceCollection>? configurePlatform = null)
    {
        // === 数据目录：**必须是第一件事** ===
        // LogSetup 会创建 logs/，一旦它先跑，AppPaths 的旧目录迁移就会看到"新位置已存在"而放弃
        // （CLAUDE.md 陷阱 8）。
        var migration = AppPaths.Initialize();

        Log.Logger = LogSetup.CreateLogger();

        // 密码管理器：别让崩溃转储把进程内存写到磁盘
        Environment.SetEnvironmentVariable("DOTNET_DbgEnableMiniDump", "0");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Fatal(ex, "Unhandled exception");
            else Log.Fatal("Unhandled exception: {Object}", e.ExceptionObject);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        if (migration is AppPaths.Migration.Migrated)
            Log.Information("Migrated data dir {From} -> {To}", AppPaths.LegacyDataDir, AppPaths.DataDir);
        else if (migration is AppPaths.Migration.Failed)
            Log.Error("Data dir migration failed; legacy dir {From} left untouched", AppPaths.LegacyDataDir);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });

        // 领域服务 / 契约接口 / 扩展桥：与 MAUI、Blazor Server 宿主共用同一份注册
        builder.Services.AddTamaServices();
#if DEBUG
        builder.Services.AddTamaDataStores(msg => Log.Debug("EFCore: {Message}", msg));
#else
        builder.Services.AddTamaDataStores();
#endif
        // 离线写入队列唯一的消费者。两个 UI 宿主都必须注册（漏了 = 离线改的东西永远推不上去）
        builder.Services.AddHostedService<SyncWorker>();

        configurePlatform?.Invoke(builder.Services);
        builder.Services.AddAvaloniaUi();

        var host = builder.Build();

        // 先 Start：SyncWorker 从这一刻开始按 5 秒节奏推离线队列
        host.Start();

        Log.Information("Tama (Avalonia) starting — data dir {DataDir}, satori={Satori}", AppPaths.DataDir, GcDiagnostics.IsSatoriActive);

        return new AppEnvironment
        {
            Host = host,
            Session = host.Services.GetRequiredService<AppSession>(),
        };
    }
}

/// <summary>
/// GC 可观测性：Satori GC 是**发布时**替换运行时（见 apps/Tama.Avalonia.Desktop 的 PublishWithSatoriGC
/// SDK），运行时只能"读出来"是不是真的换了，不能"打开"。这个类把判断收在一处，
/// 启动日志与设置页都读它——否则"到底跑的是哪个 GC"只能靠发布会话里的记忆。
/// </summary>
public static class GcDiagnostics
{
    /// <summary>Satori 会往 GC 配置里塞一个 <c>SatoriGC</c> 键（官方 README 的验证方式）。</summary>
    public static bool IsSatoriActive
    {
        get
        {
            try { return GC.GetConfigurationVariables().ContainsKey("SatoriGC"); }
            catch { return false; }
        }
    }

    public static string Describe()
    {
        var server = System.Runtime.GCSettings.IsServerGC;
        var latency = System.Runtime.GCSettings.LatencyMode;
        return IsSatoriActive
            ? $"Satori GC（{(server ? "Server 堆" : "Workstation 堆")}，LatencyMode={latency}）"
            : $"默认 .NET GC（{(server ? "Server" : "Workstation")}，LatencyMode={latency}）";
    }
}

public static class AvaloniaServiceCollectionExtensions
{
    public static IServiceCollection AddAvaloniaUi(this IServiceCollection services)
    {
        // 平台能力兜底：平台头不注册就用 Null 降级（绝不回退明文，不弹窗）
        services.AddSingleton<Tama.Core.Interfaces.IBiometricService, Tama.Services.Biometric.NullBiometricService>();

        // 注意：AppSession 由容器构造（它的依赖就是根 IServiceProvider），页面从会话作用域
        // 解析它时会拿到同一个实例。ShellViewModel 例外——它手工 new（见 App.axaml.cs）。
        services.AddSingleton<AppSession>();
        services.AddSingleton<ShellAccessor>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ClipboardService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<VaultIoService>();

        // 页面按导航创建；Scoped 依赖（DbContext 等）从 AppSession 的作用域取
        services.AddTransient<UnlockViewModel>();
        services.AddTransient<VaultViewModel>();
        services.AddTransient<GeneratorViewModel>();
        services.AddTransient<WatchtowerViewModel>();
        services.AddTransient<AccountsViewModel>();
        services.AddTransient<PasskeysViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services;
    }
}
