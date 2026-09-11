$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "Building MAUI Windows..." -ForegroundColor Cyan
dotnet build "$root\apps\Tama.App\Tama.App.csproj" -f net11.0-windows10.0.19041.0

Write-Host "`nLaunching Tama..." -ForegroundColor Green
dotnet run --project "$root\apps\Tama.App" -f net11.0-windows10.0.19041.0 --no-build
