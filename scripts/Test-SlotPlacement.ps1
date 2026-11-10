$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
# Run the complete production reader/controller against supplied native observations. This does
# not simulate Unity snapping, a purchase, undo or a save; those remain cumulative runtime checks.
$sources = @('PlacementGrid','PlacementPointer','ItemSlotReader','SlotPlacementHandler') | ForEach-Object {
    (Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot "src/Handlers/$_.cs")) -replace '(?m)^using [^;]+;\r?\n', ''
}
$placement = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/ItemPlacementHandler.cs')
function Read-Method([string]$signature) {
    $start = $placement.IndexOf($signature)
    if ($start -lt 0) { throw "Missing production method: $signature" }
    $body = $placement.IndexOf('{', $start); $depth = 1; $end = $body + 1
    while ($depth -gt 0) { if ($placement[$end] -eq '{') { $depth++ }; if ($placement[$end] -eq '}') { $depth-- }; $end++ }
    $placement.Substring($start, $end - $start)
}
$guard = @('internal static string SemanticPlacementProblem(', 'private static ItemObjectRoot GetBlockingObject(',
    'private static void ResetBlockingObjectCache(', 'private static bool IsSemanticPlacementSupport(') | ForEach-Object { Read-Method $_ }
$adapters = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/SlotPlacementAdapters.cs')
$adapters = $adapters.Replace('PRODUCTION_GUARD', ($guard -join "`n"))
$placement = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/ExistingItemEditHandler.cs')
$adapters = $adapters.Replace('PRODUCTION_MOVES_WITH', (Read-Method 'internal static bool MovesWith('))
$sources += Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomBoundaryAdapter.cs')
Add-Type -TypeDefinition ($adapters + "`n" + ($sources -join "`n")) -Language CSharp
[ParalivesAccess.Handlers.SlotChecks]::Run()
