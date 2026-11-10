# 构建 Android APK
#
# 为什么要有这个脚本（而不是让人手敲命令）：.NET Android **不读** ANDROID_HOME / ANDROID_SDK_ROOT，
# 只认 `AndroidSdkDirectory` 这个 MSBuild 属性，漏了就是 XA5300「找不到 Android SDK 目录」。
# 这脚本把"SDK 路径 + mise 里的 JDK"两件事一起处理掉。
#
# 用法：
#   ./scripts/build-android.ps1                  # Release
#   ./scripts/build-android.ps1 -Configuration Debug
#   ./scripts/build-android.ps1 -Sdk D:\Android\Sdk
#
# 前置条件：
#   dotnet workload install android        （一次性）
#   JDK 21      → 本项目 .mise.toml 里 pin 了 java = "21.0.13"，脚本走 mise exec 保证用它
#   Android SDK → 默认读 $env:ANDROID_SDK_ROOT / $env:ANDROID_HOME
#
# 产物：apps/Tama.Avalonia.Android/bin/<Configuration>/net11.0-android/com.tama.app-Signed.apk

[CmdletBinding()]
param(
    [string]$Sdk = '',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'apps/Tama.Avalonia.Android'

# SDK 路径解析：参数 → 进程环境 → 用户/机器级环境（新开的终端才有，IDE 里跑的老进程可能没有）
#              → 常见安装位置兜底。.NET Android 自己**不读**这些变量，必须显式传属性。
function Resolve-AndroidSdk {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) { return $Explicit }

    foreach ($name in 'ANDROID_SDK_ROOT', 'ANDROID_HOME') {
        $v = [Environment]::GetEnvironmentVariable($name, 'Process')
        if ([string]::IsNullOrWhiteSpace($v)) { $v = [Environment]::GetEnvironmentVariable($name, 'User') }
        if ([string]::IsNullOrWhiteSpace($v)) { $v = [Environment]::GetEnvironmentVariable($name, 'Machine') }
        if (-not [string]::IsNullOrWhiteSpace($v)) { return $v }
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk'),
        "$env:USERPROFILE\android-sdk",
        "$env:USERPROFILE\AppData\Local\Android\Sdk",
        'C:\android-sdk'
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'platforms'))) { return $c }
    }

    return ''
}

$Sdk = Resolve-AndroidSdk -Explicit $Sdk

if ([string]::IsNullOrWhiteSpace($Sdk)) {
    throw "找不到 Android SDK 路径。请设置 `$env:ANDROID_SDK_ROOT（当前终端）或用 -Sdk 指定。"
}
if (-not (Test-Path (Join-Path $Sdk 'platforms'))) {
    throw "SDK 路径不像 Android SDK（缺 platforms/）：$Sdk"
}

Write-Host "SDK    : $Sdk" -ForegroundColor DarkGray
Write-Host "Config : $Configuration" -ForegroundColor DarkGray

$buildArgs = @(
    'build', $project,
    '-c', $Configuration,
    "-p:AndroidSdkDirectory=$Sdk"
)

# JDK 交给 mise（项目 .mise.toml pin 了 21）；没有 mise 就退回调用者自己的 JAVA_HOME
$mise = Get-Command mise -ErrorAction SilentlyContinue
if ($mise) {
    Write-Host "JDK    : 走 mise exec（.mise.toml → java 21）" -ForegroundColor DarkGray
    & mise exec -- dotnet @buildArgs
} else {
    Write-Warning "没找到 mise：直接用当前 JAVA_HOME（$env:JAVA_HOME）。.NET 11 Android 需要 JDK 21。"
    & dotnet @buildArgs
}

if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }

$apkDir = Join-Path $project "bin/$Configuration/net11.0-android"
$apk = Get-ChildItem $apkDir -Filter '*-Signed.apk' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($apk) {
    Write-Host ("APK    : " + $apk.FullName + "  (" + [math]::Round($apk.Length / 1MB, 1) + " MB)") -ForegroundColor Green
} else {
    Write-Warning "构建成功但没找到 *-Signed.apk，检查 $apkDir"
}
