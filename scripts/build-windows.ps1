param(
    [string]$OutputDir
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $OutputDir) {
    $OutputDir = Join-Path $root "publish\win-x64"
}

Write-Host "=== Publish Tama (Avalonia, self-contained win-x64) ===" -ForegroundColor Cyan
# 清干净输出，避免旧文件残留（曾经因为没清而打进上一个版本的 dll）
if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
dotnet publish "$root\apps\Tama.Avalonia.Desktop\Tama.Avalonia.Desktop.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:DebugType=none `
    -o $OutputDir

$exe = Join-Path $OutputDir "Tama.Avalonia.Desktop.exe"
if (Test-Path $exe) {
    $sizeMB = [math]::Round(((Get-ChildItem -Recurse $OutputDir | Measure-Object Length -Sum).Sum) / 1MB, 1)
    Write-Host "Build: $OutputDir ($sizeMB MB)" -ForegroundColor Green
    Write-Host "  Tama.Avalonia.Desktop.exe" -ForegroundColor Green
} else {
    Write-Host "ERROR: Tama.Avalonia.Desktop.exe not found in $OutputDir!" -ForegroundColor Red
    exit 1
}
