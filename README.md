# Tama

跨平台密码管理器。本地优先，整个保险库用主密码派生的密钥直接加密

一套 Blazor 页面（`src/Tama.UI`）跑在三个宿主里：Windows / Android 原生外壳、浏览器、
以及给浏览器扩展用的 native messaging host。

## 怎么跑

需要 .NET 11 SDK（要跑 Windows / Android 外壳还需 MAUI workload）。

```powershell
# 还原 + 构建整个解决方案
dotnet build Tama.slnx

# Windows 桌面（当前主力）
./scripts/run-windows.ps1

# 浏览器模式 → http://localhost:5000
dotnet run --project apps/Tama.Api

# 测试
dotnet test tests/Tama.Tests
```

首次启动会让你设一个主密码，它直接加密本地库，忘了就没了。

数据全在 `%LOCALAPPDATA%\Tama\`，删掉该目录即等于重置。

> Android 暂时搁置：`build-android.ps1` / `run-android.ps1` 已删。平台代码和 TFM 都还在，
> 需要时自己加 `-p:EnableAndroid=true`，并且必须 `-c Release`（Debug 下会闪退）。

## 浏览器扩展（通行密钥）

通行密钥的创建与登录都由网站发起，扩展负责拦下来交给桌面端签名：

```powershell
./scripts/install-extension.ps1
```

## 感谢

灵感来自 **[Keyguard](https://keyguard.dev/)**（[AChep/keyguard-app](https://github.com/AChep/keyguard-app)）
—— 一个用 Kotlin Multiplatform 写的密码客户端

Tama 是基于 .NET / Blazor 的独立实现

## 许可

[AGPL-3.0](LICENSE)
