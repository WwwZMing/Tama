using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Serilog;
using Tama.Core;
using Tama.Core.Interfaces;
using Tama.Services;
using Tama.Services.Biometric;
using Tama.Services.Logging;
using Tama.Services.Sync;
using Tama.UI.Services;
#if ANDROID
using Tama.Platforms.Droid;
#elif WINDOWS
using Tama.Platforms.Windows;
#endif

namespace Tama;

public static class MauiProgram
{
#if WINDOWS
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);

    const int ATTACH_PARENT_PROCESS = -1;
#endif

    public static MauiApp CreateMauiApp()
    {
#if DEBUG && WINDOWS
        AttachConsole(ATTACH_PARENT_PROCESS);
#endif

        // === 数据目录：**必须是第一件事** ===
        // 唯一来源见 Tama.Core/AppPaths。这里的顺序不是风格问题：日志初始化（LogSetup）
        // 会创建 logs/ 目录，一旦它先跑，迁移就会看到"新位置已存在且非空"而放弃，
        // 用户的库永远搬不过来（实测踩到，见 CLAUDE.md 陷阱 8）。
        var migration = AppPaths.Initialize();

        Log.Logger = LogSetup.CreateLogger();

        // 禁用 .NET crash dump，防止进程内存泄漏到磁盘（密码管理器安全要求）
        Environment.SetEnvironmentVariable("DOTNET_DbgEnableMiniDump", "0");

        // 全局未处理异常兜底：渲染/UI 线程崩溃会直接杀进程，日志常常来不及写。
        // 注意：这里原先注册了**两遍** UnhandledException / UnobservedTaskException ——
        // 事件是多播的，等于每条异常写两遍日志。已删掉重复的那一组。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Log.Fatal(ex, "Unhandled exception");
            else
                Log.Fatal("Unhandled exception: {Object}", e.ExceptionObject);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Services.AddHybridWebViewDeveloperTools();
        builder.Logging.AddDebug();
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        // Blazor Hybrid：组件树在进程内渲染。
        // 业务 → 领域契约接口（下方注册，进程内直调具体服务，零 JSON）；
        // 宿主能力（原生标题栏 / Android Autofill 推送）→ IHostIntegration。
        builder.Services.AddMauiBlazorWebView();
        // 具体类型必须单独注册：Android 平台代码经 MauiHostIntegration.Current 取的是具体类型，
        // 只注册 IHostIntegration 会让 GetService<MauiHostIntegration>() 返回 null（Autofill banner 静默失效）
        builder.Services.AddSingleton<MauiHostIntegration>();
        builder.Services.AddSingleton<IHostIntegration>(sp => sp.GetRequiredService<MauiHostIntegration>());
        // MudBlazor：对话框/气泡/Snackbar 等基础服务（组件与主题在 MainLayout 里声明）
        builder.Services.AddMudServices();
        // 解锁转场的加载层协调器（乐观切换：点解锁立刻进 shell，遮罩淡出后内容逐级显示）
        builder.Services.AddScoped<ShellLoading>();

        // === 数据目录迁移结果（Initialize 已在最上面执行）===
        if (migration is AppPaths.Migration.Migrated)
            Log.Information("Migrated data dir {From} -> {To}", AppPaths.LegacyDataDir, AppPaths.DataDir);
        else if (migration is AppPaths.Migration.Failed)
            Log.Error("Data dir migration failed; legacy dir {From} left untouched", AppPaths.LegacyDataDir);

        // === 注册服务 ===
        // 领域服务 / 契约接口 / 扩展桥共用一份（Tama.Services.ServiceCollectionExtensions）
        builder.Services.AddTamaServices();

        // 两个加密 SQLite 库（连接串不带密码，见 AddTamaDataStores 的注释）
#if DEBUG
        // ⚠ **不许**传 sensitiveDataLogging: true。它让 EF 把**参数值**一起打进日志，
        //    而日志文件（%LOCALAPPDATA%\Tama\logs\tama-*.log）**没有加密**——保险库是加密的，
        //    可这样一来用户名/密码/TOTP/备注就会以明文躺在日志里（实测 465 行带值：
        //    `Executed DbCommand ... [Parameters=[@p0='…' (Size = 5)]]`）。关掉之后 EF 只打
        //    `@p0='?'`（占位符）+ SQL 正文，调试信息足够，值一个都不落盘。
        //    配套：LogSetup 的**文件** sink 只收 Information 及以上（Debug 只进控制台）。
        builder.Services.AddTamaDataStores(
            msg => Serilog.Log.Debug("EFCore: {Message}", msg));
#else
        builder.Services.AddTamaDataStores();
#endif

        // 本机回环 HTTP bridge（扩展 native messaging host 入口）：仅 Windows 需要
#if WINDOWS
        builder.Services.AddSingleton<LocalBridgeServer>();
#endif

#if ANDROID
        // Android：生物识别用 AndroidKeyStore + BiometricHelper，Passkey 用反射检测 Credential Manager
        builder.Services.AddSingleton<IBiometricService, AndroidBiometricService>();
        builder.Services.AddSingleton<IPasskeyPlatformService, AndroidPasskeyPlatformService>();
#elif WINDOWS
        // Windows：生物识别解锁 = DPAPI 保护密钥 + Windows Hello 门禁；Passkey 走 Windows Hello（UserConsentVerifier）
        builder.Services.AddSingleton<IBiometricService, WindowsBiometricService>();
        builder.Services.AddSingleton<IPasskeyPlatformService, WindowsPasskeyPlatformService>();
#else
        // 其余平台：降级为不可用（不落明文、不弹窗）
        builder.Services.AddSingleton<IBiometricService, NullBiometricService>();
        builder.Services.AddSingleton<IPasskeyPlatformService, NullPasskeyPlatformService>();
#endif

        // 后台同步 Worker
        builder.Services.AddHostedService<SyncWorker>();

        var app = builder.Build();

        // 启动本机回环 bridge（供扩展 native messaging host 调用；失败仅记日志不影响主应用）
#if WINDOWS
        var bridgeServer = app.Services.GetRequiredService<LocalBridgeServer>();
        _ = Task.Run(() => bridgeServer.StartAsync());
#endif

        return app;
    }
}
