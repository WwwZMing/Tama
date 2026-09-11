# Install/uninstall the Tama WebAuthn browser extension + native messaging host.
#
# Usage (PowerShell 7):
#   ./scripts/install-extension.ps1 -Action install [-ExtensionId <id>]   # install everything
#   ./scripts/install-extension.ps1 -Action setid -ExtensionId <id>      # update allowed_origins after loading the extension
#   ./scripts/install-extension.ps1 -Action uninstall                    # remove registry entries + files
#
# Steps after install:
#   1. The script opens edge://extensions. Click "Developer mode" then
#      "Load unpacked" and pick:  %LOCALAPPDATA%\Tama\extension
#   2. Copy the extension ID shown on the card.
#   3. Run:  ./scripts/install-extension.ps1 -Action setid -ExtensionId <id>
#   4. Restart the browser, then visit any WebAuthn site (e.g. Microsoft account login).

param(
    [ValidateSet("install", "setid", "uninstall")]
    [string]$Action = "install",
    [string]$ExtensionId = "",
    [string]$NativeHostName = "com.tama.webauthn"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$baseDir = Join-Path $env:LOCALAPPDATA "Tama"
$extensionDir = Join-Path $baseDir "extension"
$hostDir = Join-Path $baseDir "native-host"
$hostManifest = Join-Path $hostDir "$NativeHostName.json"
$regBrowsers = @(
    @{ Name = "Chrome"; Key = "HKCU:\Software\Google\Chrome\NativeMessagingHosts" },
    @{ Name = "Edge";   Key = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts" }
)

function Write-Manifest {
    param([string]$Id)
    # 注意：PowerShell 的 if 语句输出集合会被展开为标量，必须用 @(...) 强制数组——
    # Chrome/Edge 校验 allowed_origins 必须是数组，字符串会被拒绝连接 native host
    $allowed = @(if ($Id) { "chrome-extension://$Id/" } else { "chrome-extension://*/" })
    $manifest = @{
        name = $NativeHostName
        description = "Tama passkey bridge host"
        path = Join-Path $hostDir "Tama.NativeHost.exe"
        type = "stdio"
        allowed_origins = $allowed
    } | ConvertTo-Json -Depth 4
    Set-Content -Path $hostManifest -Value $manifest -Encoding UTF8
    Write-Host "[OK] native host manifest -> $hostManifest"
    if (-not $Id) {
        Write-Host "  (allowed_origins uses wildcard; if the browser rejects the host, set the real ID:)"
        Write-Host "    ./scripts/install-extension.ps1 -Action setid -ExtensionId <id>"
    }
}

if ($Action -eq "uninstall") {
    foreach ($b in $regBrowsers) {
        $key = Join-Path $b.Key $NativeHostName
        if (Test-Path $key) { Remove-Item $key -Recurse -Force }
        Write-Host "[OK] removed $($b.Name) registry entry"
    }
    foreach ($p in @($hostDir, $extensionDir)) {
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
    Write-Host "[OK] removed $hostDir and $extensionDir"
    exit 0
}

if ($Action -eq "setid") {
    if (-not $ExtensionId) { throw "ExtensionId is required for setid" }
    if (-not (Test-Path $hostManifest)) { throw "manifest not found - run install first" }
    Write-Manifest -Id $ExtensionId
    Write-Host "[DONE] restart your browser for the change to take effect"
    exit 0
}

# === install ===

# 1) publish NativeHost into %LOCALAPPDATA%\Tama\native-host
Write-Host "[1/5] Publishing Tama.NativeHost..."
New-Item -ItemType Directory -Force -Path $hostDir | Out-Null
dotnet publish (Join-Path $root "apps\Tama.NativeHost\Tama.NativeHost.csproj") `
    -c Release -o $hostDir --nologo -v q | Out-Null
if (-not (Test-Path (Join-Path $hostDir "Tama.NativeHost.exe"))) {
    throw "NativeHost publish failed - exe not found"
}
Write-Host "  -> $(Join-Path $hostDir 'Tama.NativeHost.exe')"

# 2) copy extension
Write-Host "[2/5] Copying extension..."
if (Test-Path $extensionDir) { Remove-Item $extensionDir -Recurse -Force }
Copy-Item -Recurse (Join-Path $root "extensions\tama-webauthn") $extensionDir
Write-Host "  -> $extensionDir"

# 3) write native host manifest (wildcard origins until real ID is set)
Write-Host "[3/5] Writing native host manifest..."
Write-Manifest -Id $ExtensionId

# 4) registry
Write-Host "[4/5] Registering native messaging host..."
foreach ($b in $regBrowsers) {
    $key = Join-Path $b.Key $NativeHostName
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name "(default)" -Value $hostManifest -Force | Out-Null
    Write-Host "  [OK] $($b.Name): $key -> $hostManifest"
}

# 5) open extension management page
Write-Host "[5/5] Opening extension page..."
Start-Process "msedge" "edge://extensions"

Write-Host ""
Write-Host "========== NEXT STEPS ==========" -ForegroundColor Green
Write-Host "1. On the edge://extensions page: enable 'Developer mode', click 'Load unpacked',"
Write-Host "   select: $extensionDir"
Write-Host "2. Copy the extension ID from the card."
Write-Host "3. Run:  ./scripts/install-extension.ps1 -Action setid -ExtensionId <id>"
Write-Host "4. Restart Edge, then try a WebAuthn login (e.g. https://account.microsoft.com)"
Write-Host "================================="
