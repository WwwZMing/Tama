param(
    [string]$SourceDir
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $SourceDir) {
    $SourceDir = Join-Path $root "publish\win-x64"
}

$exe = Join-Path $SourceDir "Tama.exe"
if (-not (Test-Path $exe)) {
    Write-Host "ERROR: Tama.exe not found in $SourceDir" -ForegroundColor Red
    Write-Host "Run .\scripts\build-windows.ps1 first." -ForegroundColor Yellow
    exit 1
}

$installDir = "$env:LOCALAPPDATA\Programs\Tama"
$shortcutPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Tama.lnk"
$desktopPath = [Environment]::GetFolderPath("Desktop") + "\Tama.lnk"

Write-Host "=== Installing Tama ===" -ForegroundColor Cyan

# Copy files
Write-Host "[1/3] Copying to $installDir ..."
if (Test-Path $installDir) { Remove-Item -Recurse -Force $installDir }
Copy-Item -Recurse $SourceDir $installDir

# Create Start Menu shortcut
Write-Host "[2/3] Creating Start Menu shortcut ..."
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($shortcutPath)
$Shortcut.TargetPath = Join-Path $installDir "Tama.exe"
$Shortcut.WorkingDirectory = $installDir
$Shortcut.IconLocation = Join-Path $installDir "Tama.exe,0"
$Shortcut.Save()

# Create Desktop shortcut
Write-Host "[3/3] Creating Desktop shortcut ..."
$Shortcut = $WshShell.CreateShortcut($desktopPath)
$Shortcut.TargetPath = Join-Path $installDir "Tama.exe"
$Shortcut.WorkingDirectory = $installDir
$Shortcut.IconLocation = Join-Path $installDir "Tama.exe,0"
$Shortcut.Save()

Write-Host ""
Write-Host "INSTALLED!" -ForegroundColor Green
Write-Host "  Location : $installDir" -ForegroundColor Green
Write-Host "  Start Menu: Tama" -ForegroundColor Green
Write-Host "  Desktop  : Tama" -ForegroundColor Green
