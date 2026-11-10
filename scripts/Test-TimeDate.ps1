$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\TimeDateHandler.cs')
$fixture = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures\TimeDateAdapters.cs')
Add-Type -TypeDefinition ($source + $fixture) -Language CSharp
[TimeDateChecks]::Run()
