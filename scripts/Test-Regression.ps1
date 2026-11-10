[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
# Each suite compiles production C# with its own native adapters. Isolate Add-Type assemblies in
# separate processes and keep them sequential, matching the supported single-worker build path.
$shell = (Get-Process -Id $PID).Path
$suites = Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'Test-*.ps1' |
    Where-Object { $_.Name -ne 'Test-Regression.ps1' } | Sort-Object Name
$failed = @()
foreach ($suite in $suites) {
    Write-Host "Running $($suite.Name)"
    & $shell -NoProfile -File $suite.FullName
    if ($LASTEXITCODE -ne 0) { $failed += $suite.Name }
}
if ($failed.Count -gt 0) { throw "Failed suites: $($failed -join ', ')" }
Write-Host "All $($suites.Count) regression suites passed. Native gameplay and audible delivery still require in-game verification."
