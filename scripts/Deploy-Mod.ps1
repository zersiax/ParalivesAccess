<#
.SYNOPSIS
    Deploys ParalivesAccess.dll into the game's BepInEx plugins folder.

.DESCRIPTION
    The only supported way to deploy this project.

    Refuses to copy while Paralives is running. Windows locks a loaded DLL, so the copy would
    either fail outright or - worse on a slow filesystem - appear to succeed while the game keeps
    running the old code. Silently testing a stale build is exactly the kind of wasted round trip
    this project cannot afford, since the user cannot see that anything is wrong.

    Also verifies BepInEx is actually installed, and reports the deployed file's timestamp so a
    stale deploy is visible at a glance.

.PARAMETER Configuration
    Release (default) or Debug. Must match what Build-Mod.ps1 produced.

.PARAMETER GameDir
    Game directory. Defaults to the Steam install.

.PARAMETER Force
    Deploy even if Paralives appears to be running. Use only if you know the game is closed
    and the process check is a false positive.

.EXAMPLE
    .\scripts\Deploy-Mod.ps1
    .\scripts\Deploy-Mod.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Paralives',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceDll = Join-Path $projectRoot "bin\$Configuration\net472\ParalivesAccess.dll"
$pluginsDir = Join-Path $GameDir 'BepInEx\plugins'

if (-not (Test-Path $sourceDll)) {
    Write-Error "Built DLL not found: $sourceDll`nRun .\scripts\Build-Mod.ps1 -Configuration $Configuration first."
    exit 1
}

if (-not (Test-Path $GameDir)) {
    Write-Error "Game directory not found: $GameDir"
    exit 1
}

if (-not (Test-Path (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) {
    Write-Error "BepInEx is not installed in $GameDir. Expected BepInEx\core\BepInEx.dll."
    exit 1
}

$running = @(Get-Process -Name 'Paralives' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0 -and -not $Force) {
    Write-Host "Paralives is running (PID $($running[0].Id))." -ForegroundColor Red
    Write-Host "Close the game before deploying - a loaded DLL is locked, and deploying now" -ForegroundColor Yellow
    Write-Host "risks testing the OLD build without noticing. Use -Force to override." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $pluginsDir)) {
    Write-Host "Creating $pluginsDir"
    New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null
}

$targetDll = Join-Path $pluginsDir 'ParalivesAccess.dll'

try {
    Copy-Item $sourceDll $targetDll -Force
}
catch {
    Write-Host "DEPLOY FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "If the file is locked, the game is probably still running." -ForegroundColor Yellow
    exit 1
}

$item = Get-Item $targetDll
$sizeKb = [math]::Round($item.Length / 1KB, 1)

Write-Host "DEPLOYED - $sizeKb KB at $($item.LastWriteTime.ToString('HH:mm:ss'))" -ForegroundColor Green
Write-Host "  $targetDll"
Write-Host ""
Write-Host "Log after running:  $GameDir\BepInEx\LogOutput.log"
