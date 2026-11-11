# Tama

跨平台密码管理器。本地优先，整个保险库用主密码派生的密钥直接加密。

一套 Avalonia UI（`src/Tama.Avalonia`）跑在 Windows 桌面与 Android 两个宿主里；
浏览器扩展通过 native messaging host 接入通行密钥通道。

## 怎么跑

需要 .NET 11 SDK（要构建 Android 还需要 android workload，见下）。

```powershell
# 还原 + 构建整个解决方案
dotnet build Tama.slnx

# 桌面（开发时最常用）
dotnet run --project apps/Tama.Avalonia.Desktop
# 或者
./scripts/run-windows.ps1

# 自包含发布（Satori GC）→ publish/satori-win-x64
./scripts/publish-windows-satori.ps1

# 普通自包含发布 → publish/win-x64
./scripts/build-windows.ps1

# 测试
dotnet test tests/Tama.Tests
```

首次启动会让你设一个主密码，它直接加密本地库，忘了就没了。

数据全在 `%LOCALAPPDATA%\Tama\`（可用 `TAMA_DATA_DIR` 重定向），删掉该目录即等于重置。

### Android

`apps/Tama.Avalonia.Android` **刻意不进 `Tama.slnx`**：没装 android workload 的机器上
整个解决方案会构建失败。它单独构建：

```powershell
./scripts/build-android.ps1              # Release → com.tama.app-Signed.apk
./scripts/build-android.ps1 -Configuration Debug
```

需要 Android SDK（`-Sdk` 指定或用 `ANDROID_SDK_ROOT`）与 JDK 21；仓库根的 `.mise.toml`
已经把这个项目 pin 到 `java = "21.0.13"`，脚本会经 `mise exec` 注入 `JAVA_HOME`。

## 浏览器扩展（通行密钥）

通行密钥的创建与登录都由网站发起，扩展负责拦下来交给桌面端签名：

```powershell
./scripts/install-extension.ps1
```

## 项目结构

```
src/Tama.Core        领域契约与模型（纯逻辑，零依赖）
src/Tama.Data        EF Core + SQLite（加密库）
src/Tama.Services    业务服务（认证、保险库、同步、导入导出、WebAuthn…）
src/Tama.Avalonia    Avalonia UI（页面、样式、会话作用域、浮层对话框）
apps/Tama.Avalonia.Desktop   Windows 宿主（Windows Hello、DPAPI、回环 bridge、Satori GC）
apps/Tama.Avalonia.Android   Android 宿主（单视图）
apps/Tama.NativeHost         浏览器扩展用的 native messaging host
extensions/tama-webauthn     浏览器扩展
tests/Tama.Tests             单元测试
```

> 2026-11-12：Blazor 组件库（`src/Tama.UI`）与三个旧宿主（MAUI `Tama.App`、
> Blazor Server `Tama.Api`、Photino `Tama.Desktop`）已删除，UI 只剩 Avalonia 一套。

## 感谢

灵感来自 **[Keyguard](https://keyguard.dev/)**（[AChep/keyguard-app](https://github.com/AChep/keyguard-app)）
—— 一个用 Kotlin Multiplatform 写的密码客户端。

Tama 是基于 .NET / Avalonia 的独立实现。

## 许可

[AGPL-3.0](LICENSE)
