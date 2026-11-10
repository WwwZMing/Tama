# 用 Satori GC 发布 Windows 版（自包含）
#
# 为什么必须是"自包含"：Satori 不是运行时开关，而是**整套替换的运行时**（自定义 GC 的 CoreCLR）。
# PublishWithSatoriGC SDK 只在自包含发布里把它塞进去；框架依赖发布拿不到（要用 AppHostRelativeDotNet
# 指向一个自己装的 Satori .NET，见上游 README，这里不做）。
#
# 用法：
#   ./scripts/publish-windows-satori.ps1                 # win-x64，输出到 publish/satori-win-x64
#   ./scripts/publish-windows-satori.ps1 -Arch arm64     # win-arm64
#   ./scripts/publish-windows-satori.ps1 -Stock          # 对照：同一份代码用默认 GC 发布
#
# 验证：跑起来看日志里 "satori=True"，或设置页「运行信息」显示 Satori GC。
#       也可以在代码里查 GC.GetConfigurationVariables().ContainsKey("SatoriGC")（GcDiagnostics 就是干这个的）。

[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [switch]$Stock
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'apps/Tama.Avalonia.Desktop'
$rid = "win-$Arch"
$outDir = if ($Stock) { Join-Path $repo "publish/stock-$rid" } else { Join-Path $repo "publish/satori-$rid" }

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $rid,
    '--self-contained', 'true',
    '-p:DebugType=none',
    '-o', $outDir
)
if ($Stock) { $publishArgs += '-p:UseSatoriGC=false' }

Write-Host "publishing $rid -> $outDir" -ForegroundColor Cyan
Write-Host ("dotnet " + ($publishArgs -join ' ')) -ForegroundColor DarkGray

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $outDir 'Tama.Avalonia.Desktop.exe'
if (-not (Test-Path $exe)) { throw "publish 目录里没有 $exe —— 检查 RID 与 OutputType" }

Write-Host "done: $exe" -ForegroundColor Green
Write-Host "跑一下确认 GC：日志里应有 satori=True（或在设置页看「运行信息」）" -ForegroundColor Yellow
