$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
# Compile the actual key handler, native layout readers, F3 reader and room-content path together.
# Adapters supply native observations only. This cannot verify Unity timing or audible playback.
$sources = @('PlacementGrid','PlacementGridReader','SpatialOutline','ZoneGeometry','RoomBoundaryConstraint','PlacementLocationReader','WallFaceReader','WallSpanReader',
    'ItemFootprintReader','RoomSpatialReader','LotLayoutReader','LotDescriptionHandler','BuildCameraHandler','RoomLabelData','RoomLabelHandler') | ForEach-Object {
    (Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot "src/Handlers/$_.cs")) -replace '(?m)^using [^;]+;\r?\n', ''
}
$adapters = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/LotDescriptionAdapters.cs')
$adapters = "using System.Runtime.Serialization;`n" + $adapters
$adapters = "using System.IO;`nusing HarmonyLib;`n" + $adapters.Replace('public class AssetLot {', 'public partial class AssetLot {').Replace('public static class CoordinateEntryHandler {', 'public static partial class CoordinateEntryHandler {').Replace('public static class Plugin {', 'public static class Plugin { public const string PluginGuid="test";')
$adapters += Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomLabelAdapters.cs')
$loc = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Loc.cs')
$strings = [regex]::Matches($loc, 'Add\("(?:\\.|[^"\\])*",\s*"(?:\\.|[^"\\])*"\);') |
    ForEach-Object { 'Text.' + $_.Value }
$adapters = $adapters.Replace('LOCALIZED_STRINGS', ($strings -join "`n"))
$checks = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/LotDescriptionChecks.cs')
$checks += Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomLabelChecks.cs')
$checks += Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomBoundaryChecks.cs')
$explorer = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/LotObjectHandler.cs')
$start = $explorer.IndexOf('internal void AssignRoomOrdinals(')
if ($start -lt 0) { throw 'Missing production F7 room ordinal assignment' }
$body = $explorer.IndexOf('{', $start); $depth = 1; $end = $body + 1
while ($depth -gt 0) { if ($explorer[$end] -eq '{') { $depth++ }; if ($explorer[$end] -eq '}') { $depth-- }; $end++ }
$checks += (Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomDirectionChecks.cs')).Replace('F7_ROOM_ORDINALS', $explorer.Substring($start, $end - $start))
$placement = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/ItemPlacementHandler.cs')
$framingMethods = @('private static bool TryFindInitialOwnedLotPoint(', 'private static bool IsFinite(') | ForEach-Object {
    $start = $placement.IndexOf($_)
    if ($start -lt 0) { throw "Missing production method: $_" }
    $body = $placement.IndexOf('{', $start); $depth = 1; $end = $body + 1
    while ($depth -gt 0) { if ($placement[$end] -eq '{') { $depth++ }; if ($placement[$end] -eq '}') { $depth-- }; $end++ }
    $placement.Substring($start, $end - $start)
}
$candidates = [regex]::Match($placement, '(?s)private static readonly Vector2\[\] InitialCursorCandidates\s*=.*?};').Value
if (!$candidates) { throw 'Missing native framing candidate declaration' }
$checks += (Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/PlacementFramingChecks.cs')).Replace('FRAMING_METHODS', ($framingMethods -join "`n")).Replace('FRAMING_CANDIDATES', $candidates)
Add-Type -TypeDefinition ($adapters + "`n" + ($sources -join "`n") + "`n" + $checks) -Language CSharp
[ParalivesAccess.Handlers.LotDescriptionChecks]::Run()
[ParalivesAccess.Handlers.LotDescriptionChecks]::RunFraming()
[ParalivesAccess.Handlers.LotDescriptionChecks]::RunRoomDirections()
$evidenceRoot = Join-Path $projectRoot 'bin/room-label-checks'
[ParalivesAccess.Handlers.LotDescriptionChecks]::RunRoomNames($evidenceRoot)
[ParalivesAccess.Handlers.LotDescriptionChecks]::RunBoundaries($evidenceRoot)
