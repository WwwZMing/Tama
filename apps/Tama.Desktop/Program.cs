using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Photino.Blazor;
using Tama.Core;
using Tama.Core.Interfaces;
using Tama.Services;
using Tama.Services.Biometric;
using Tama.Services.Platforms.Linux;
using Tama.Services.Sync;
using Tama.UI.Services;

// Linux 桌面宿主：进程内 Blazor（同 MAUI 的 Hybrid 模型），不起 HTTP 服务器。
// 注册清单与 apps/Tama.Api/Program.cs 一致，只是把 Kestrel 换成 Photino 原生窗口。

// 数据目录：**必须是第一件事**（同其他宿主，早于任何会创建目录的动作）。
AppPaths.Initialize();

var builder = PhotinoBlazorAppBuilder.CreateDefault(args);

// === 领域服务 / 契约接口 / 扩展桥：与 MAUI、Tama.Api 共用同一份注册 ===
builder.Services.AddTamaServices();
builder.Services.AddTamaDataStores();

// 平台能力：Linux 上用 fprintd 做指纹解锁 / 通行密钥确认；
// 其余平台（Photino 在 Windows/macOS 上也能跑）没有原生通道，降级为不可用——
// 不落明文、不弹窗（Windows 的 DPAPI + Windows Hello 实现在 MAUI 宿主那侧）。
if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IBiometricService, LinuxBiometricService>();
    builder.Services.AddSingleton<IPasskeyPlatformService, LinuxPasskeyPlatformService>();
}
else
{
    builder.Services.AddSingleton<IBiometricService, NullBiometricService>();
    builder.Services.AddSingleton<IPasskeyPlatformService, NullPasskeyPlatformService>();
}
builder.Services.AddSingleton<IHostIntegration, NoOpHostIntegration>();

// 离线写入队列唯一的消费者——漏注册的话离线改的东西永远推不上去
builder.Services.AddHostedService<SyncWorker>();

builder.Services.AddMudServices();
builder.Services.AddScoped<ShellLoading>();

// 整个 UI 就是 Tama.UI 的那棵树，与 MAUI 宿主同一个根组件
builder.RootComponents.Add<Tama.UI.Main>("#app");

var app = builder.Build();

app.MainWindow
   .SetTitle("Tama")
   .SetSize(1200, 800)
   .SetUseOsDefaultSize(false);

app.Run();
