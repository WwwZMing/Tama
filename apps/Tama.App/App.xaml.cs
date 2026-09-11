using Tama.Core.Interfaces;
using Tama.Data.Database;
using Tama.Services;
using Tama.Services.Auth;

namespace Tama;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;

#if WINDOWS
        // WebView2 在 unpackaged 模式下需要可写的用户数据目录
        var userDataFolder = Path.Combine(FileSystem.AppDataDirectory, "WebView2");
        Directory.CreateDirectory(userDataFolder);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
#endif

        // 检查 vault 是否已设置（fingerprint.json 是否存在）
        var authSession = _services.GetRequiredService<IAuthSession>();
        authSession.IsSetupAsync().Wait();
        Console.WriteLine($"[Tama] Auth: IsSetup={authSession.IsSetup}, IsUnlocked={authSession.IsUnlocked}");

        // 后台预热 EF 模型：否则模型构建的数百毫秒会落在首次解锁上（见 DbModelWarmup 注释）
        _ = Task.Run(() => DbModelWarmup.WarmUp(_services));
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var host = _services.GetRequiredService<Tama.UI.Services.IHostIntegration>();
        var window = new Window(new MainPage(host))
        {
            TitleBar = new TitleBar
            {
                Title = "Tama",
                BackgroundColor = Color.Parse("#f8fafc"),
                ForegroundColor = Color.Parse("#1e293b")
            }
        };
        return window;
    }

#if ANDROID
    /// <summary>
    /// 退到后台即锁定：HyperOS 等 ROM 划掉任务/按 Home 后进程往往仍存活，
    /// 内存解锁状态不丢导致重开直接进主界面——"关闭 app 是否锁定"完全不可预期。
    /// 统一为后台即锁（密码管理器常态），重开走解锁页（自动弹指纹）。
    /// </summary>
    protected override void OnSleep()
    {
        base.OnSleep();
        try
        {
            var authSession = _services.GetRequiredService<IAuthSession>();
            if (authSession.IsUnlocked)
            {
                authSession.Lock();
                Console.WriteLine("[Tama] Auto-locked on background (OnSleep)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tama] OnSleep lock failed: {ex.Message}");
        }
    }
#endif
}
