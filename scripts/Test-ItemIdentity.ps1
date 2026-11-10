$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\ItemDescription.cs')
$start = $source.IndexOf('private static string BuildProductIdentity(')
if ($start -lt 0) { throw 'Missing production identity composer' }
$body = $source.IndexOf('{', $start); $depth = 1; $end = $body + 1
while ($depth -gt 0) {
    if ($source[$end] -eq '{') { $depth++ }
    if ($source[$end] -eq '}') { $depth-- }
    $end++
}
$fixture = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures\ItemIdentityAdapters.cs')
$fixture = $fixture.Replace('IDENTITY_COMPOSER', $source.Substring($start, $end - $start))
$reader = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\ItemInteractionNameReader.cs')
Add-Type -TypeDefinition ($reader + $fixture) -Language CSharp
[ItemIdentityChecks]::Run()
