<#
.SYNOPSIS
    Builds ParalivesAccess from source and optionally installs the resulting plugin.
.DESCRIPTION
    Requires a .NET SDK capable of building net472 and an installed copy of Paralives
    with BepInEx 5.4.23.5. References are read from that installation, never downloaded
    or distributed with the source. Build and compiler concurrency is limited to one.
.EXAMPLE
    .\scripts\Build-Mod.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Paralives'
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Paralives',
    [switch]$Deploy,
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectFile = Join-Path $projectRoot 'ParalivesAccess.csproj'
if (!(Test-Path -LiteralPath $projectFile)) { throw "Project not found: $projectFile" }
if ($Clean) {
    foreach ($directory in @('bin', 'obj')) {
        $outputDirectory = [IO.Path]::GetFullPath((Join-Path $projectRoot $directory))
        if (![string]::Equals([IO.Path]::GetDirectoryName($outputDirectory), $projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Build output is outside the project: $outputDirectory"
        }
        if (Test-Path -LiteralPath $outputDirectory) { Remove-Item -LiteralPath $outputDirectory -Recurse -Force }
    }
}
& dotnet build $projectFile -c $Configuration -m:1 --nologo "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$outputDll = Join-Path $projectRoot "bin/$Configuration/net472/ParalivesAccess.dll"
if (!(Test-Path -LiteralPath $outputDll)) { throw "Build output not found: $outputDll" }
Write-Host "Built: $outputDll"
if ($Deploy) {
    & (Join-Path $PSScriptRoot 'Deploy-Mod.ps1') -Configuration $Configuration -GameDir $GameDir
    exit $LASTEXITCODE
}