param(
    [string]$OutputDir
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $OutputDir) {
    $OutputDir = Join-Path $root "publish\win-x64"
}

Write-Host "=== Publish MAUI Windows (Release) ===" -ForegroundColor Cyan
# Clean stale output to avoid leftover files
if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
dotnet publish "$root\apps\Tama.App\Tama.App.csproj" `
    -f net11.0-windows10.0.19041.0 `
    -c Release `
    -o $OutputDir

$exe = Join-Path $OutputDir "Tama.exe"
if (Test-Path $exe) {
    $sizeMB = [math]::Round(((Get-ChildItem -Recurse $OutputDir | Measure-Object Length -Sum).Sum) / 1MB, 1)
    Write-Host "Build: $OutputDir ($sizeMB MB)" -ForegroundColor Green
    Write-Host "  Tama.exe" -ForegroundColor Green
} else {
    Write-Host "ERROR: Tama.exe not found in $OutputDir!" -ForegroundColor Red
    exit 1
}
