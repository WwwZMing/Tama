using Tama.Api.Components;
using Tama.Core;
using Tama.Core.Interfaces;
using Tama.Services;
using Tama.Services.Biometric;
using Tama.UI.Services;
using Microsoft.AspNetCore.DataProtection;
using MudBlazor.Services;

// 浏览器 / 无 WebView 平台的 HTTP 宿主（2026-09-06 完成 npm/ui 退役）。
// 两张面孔：
//   ① Blazor Server UI —— 复用 Tama.UI 共享组件库（与 MAUI Hybrid 同一套页面），
//      业务走领域契约接口（进程内直调具体服务，零 JSON）；
//   ② /api/bridge —— 浏览器扩展 native host 的 HTTP 协议入口。
// 数据库密钥不在此处交互输入：由 AuthSessionService 在 auth/setup、auth/unlock 后写入 DatabaseKeyService，
// DbContext 采用工厂模式延迟解析，解锁前不可用（与 MauiProgram 行为一致）。

// === 数据目录：**必须是第一件事** ===
// 唯一来源见 Tama.Core/AppPaths；Initialize 顺带完成旧目录一次性迁移
// （%LOCALAPPDATA%\Keyguard → %LOCALAPPDATA%\Tama，含库文件名）。
// 顺序要紧：任何会创建数据目录的动作（日志、DataProtection…）跑在它前面，
// 迁移就会看到"新位置已存在"而放弃，用户的库永远搬不过来。
AppPaths.Initialize();

var builder = WebApplication.CreateBuilder(args);

// === DataProtection：默认用户级密钥环在本机会 DPAPI 解密失败（跨上下文创建的旧 key），
//     Blazor Server 的电路 ID/SSR 保护依赖它——改用应用数据目录下的独立密钥环 ===
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(AppPaths.DataProtectionKeysDir));

// === 领域服务 / 契约接口 / 扩展桥：与 MAUI 宿主共用同一份注册 ===
builder.Services.AddTamaServices();

// 两个加密 SQLite 库（连接串不带密码，见 AddTamaDataStores 的注释）。
// 浏览器宿主刻意不开敏感数据日志：密码管理器不该把派生值写进日志。
builder.Services.AddTamaDataStores();

// 浏览器模式无原生通道：生物识别/Passkey 降级为不可用（安全：不落明文、不弹窗）
builder.Services.AddSingleton<IBiometricService, NullBiometricService>();
builder.Services.AddSingleton<IPasskeyPlatformService, NullPasskeyPlatformService>();

// 浏览器宿主的宿主集成：无原生标题栏、无 Autofill 通道——全部静默降级
builder.Services.AddSingleton<IHostIntegration, NoOpHostIntegration>();

// 离线写入队列的消费者（SyncWorker）+ 定时全量拉取。**这个宿主以前没注册它**，
// 后果是浏览器模式下离线写的东西（条目、文件夹）永远推不出去——没有任何入口会去消费队列。
builder.Services.AddHostedService<Tama.Services.Sync.SyncWorker>();
builder.Services.AddMudServices();
// 解锁转场的加载层协调器（乐观切换：点解锁立刻进 shell，遮罩淡出后内容逐级显示）
builder.Services.AddScoped<ShellLoading>();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers()
    .AddJsonOptions(opts => opts.JsonSerializerOptions.PropertyNameCaseInsensitive = true);

var app = builder.Build();

// 后台预热 EF 模型：否则模型构建的数百毫秒会落在首次解锁上（见 DbModelWarmup 注释）
_ = Task.Run(() => DbModelWarmup.WarmUp(app.Services));

app.UseStaticFiles();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

