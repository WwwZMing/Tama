$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "Building Tama (Avalonia Windows)..." -ForegroundColor Cyan
dotnet build "$root\apps\Tama.Avalonia.Desktop\Tama.Avalonia.Desktop.csproj"

Write-Host "`nLaunching Tama..." -ForegroundColor Green
dotnet run --project "$root\apps\Tama.Avalonia.Desktop" --no-build
