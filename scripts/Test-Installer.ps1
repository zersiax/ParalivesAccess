[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$EvidenceDirectory = (Join-Path $PSScriptRoot ('..\artifacts\installer-tests-' + [guid]::NewGuid().ToString('N'))),
    [switch]$LiveGitHub
)
$ErrorActionPreference = 'Stop'
& dotnet build (Join-Path $PSScriptRoot '..\installer\tests\InstallerChecks.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Installer test build failed.' }
$runner = Join-Path $PSScriptRoot '..\installer\tests\bin\Release\net48\InstallerChecks.exe'
$testArguments = @([IO.Path]::GetFullPath($EvidenceDirectory), [IO.Path]::GetFullPath($Package))
if ($LiveGitHub) { $testArguments += '--live' }
& $runner @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Installer checks failed.' }
$request = Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'junction-request.txt')
$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
foreach ($candidate in $request) {
    if (![IO.Path]::GetFullPath($candidate).StartsWith($evidenceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected junction test target.' }
}
New-Item -ItemType Junction -Path (Join-Path $request[0] 'BepInEx\core') -Target $request[1] | Out-Null
& $runner '--junction' $request[0] ([IO.Path]::GetFullPath($Package)) $evidenceRoot
if ($LASTEXITCODE -ne 0) { throw 'Installer junction checks failed.' }
