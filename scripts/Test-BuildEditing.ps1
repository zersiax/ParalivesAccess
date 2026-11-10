$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sources = @('PlacementGrid','WallFaceReader','ExistingItemEditHandler','CoordinateEntryHandler','ItemFootprintReader','RoomSpatialReader',
    'WallSpanReader','DoorPlacementReader','RoomPlacementGrid','FacingTargetHandler','PlacementActionsHandler','ItemSlotReader',
    'ItemAlignmentHandler','StructureEditingHandler','ConstructionGeometry','RectangleWallPlan','SharedWallBoundary','VirtualPanel','RoomExtensionReader') | ForEach-Object {
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
# Reuse the native placement adapters, not the slot test scenarios. Extend only the native APIs
# needed by this suite; all edit, form, footprint and room policies compile from production.
$adapters = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/SlotPlacementAdapters.cs')
$adapters = $adapters.Substring(0, $adapters.IndexOf(' public static class SlotChecks {')) + "`n}"
$adapters = [regex]::Replace($adapters, '(?s) // EDITING_STUBS_START.*? // EDITING_STUBS_END', '')
$adapters = "using System.Globalization;`n" + $adapters.Replace('PRODUCTION_GUARD', ($guard -join "`n"))
foreach ($type in @('ItemObjectRoot','ItemObjectInPlacement','Player','ZoneManager','Transform','InputManager','AssetLot')) {
    $adapters = $adapters.Replace("public class $type {", "public partial class $type {")
}
foreach ($type in @('WorldTargeting','ItemPlacementHandler','ItemDescription','AccessTools')) {
    $adapters = $adapters.Replace("public static class $type {", "public static partial class $type {")
}
$adapters = $adapters.Replace('public struct Vector3 {', 'public partial struct Vector3 {')
$adapters = $adapters.Replace('public struct Quaternion {', 'public partial struct Quaternion {')
$adapters = $adapters.Replace('public class HybridPlayer {', 'public class HybridPlayer { public InputButton ButtonSmallRotateItemLeft=new InputButton(),ButtonSmallRotateItemRight=new InputButton(),ButtonLargeRotateItem=new InputButton();')
$adapters = $adapters.Replace('public enum Key {Semicolon,Home}', 'public enum Key {Semicolon,Home,F10,F11,None}')
$adapters = $adapters.Replace('public class BuildModeCatalogTags {', 'public partial class BuildModeCatalogTags {')
$adapters = $adapters.Replace('public KeyState rightArrowKey=', 'public KeyState escapeKey=new KeyState();public KeyState rightArrowKey=')
$adapters = $adapters.Replace('internal static class PlacementGridReader {', 'internal static partial class PlacementGridReader {')
$adapters = $adapters.Replace('public class GameObject {', 'public partial class GameObject { public bool activeInHierarchy=true;')
$adapters = $adapters.Replace('public class MoneyManager {', 'public partial class MoneyManager {')
$adapters = $adapters.Replace('public static class ScreenReader {', 'public static partial class ScreenReader {')
$adapters = $adapters.Replace('public enum GameStates {BuildMode,LiveMode}', 'public enum GameStates {BuildMode,LiveMode,PlaceSegment}')
$adapters = $adapters.Replace('public List<Patch> Prefixes=', 'public List<Patch> Postfixes=new List<Patch>();public List<Patch> Prefixes=')
$adapters = $adapters.Replace('HarmonyMethod prefix=null,HarmonyMethod finalizer=null', 'HarmonyMethod prefix=null,HarmonyMethod postfix=null,HarmonyMethod finalizer=null')
$adapters = $adapters.Replace('var p=new Patches();', 'var p=GetPatchInfo(m)??new Patches();if(postfix!=null)p.Postfixes.Add(new Patch{PatchMethod=postfix.Method});')
$extra = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/BuildEditingAdapters.cs')
$extra = $extra.Replace('PRODUCTION_FINISH', (Read-Method 'private static void FinishTrackedPreview('))
$extra = $extra.Replace('public static class UI {', 'public static partial class UI {')
$structures = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/StructureEditingAdapters.cs')
$structures = $structures.Replace('public static class MeasurementUnitService {', 'public static partial class MeasurementUnitService {')
$extensions = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomExtensionAdapters.cs')
$explorer = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/LotObjectHandler.cs')
$explorerMembers = @('private static void OpenStructureWalls(', 'private static void OpenStructureWall(',
    'private static void OpenStructureFloors(', 'private static void OpenStructureFloor(', 'private static void OpenStructureReview(',
    'private static void OpenRoomExtension(', 'private static void OpenExtensionFromWall(', 'private static void BeginRoomExtension(',
    'private static void HandleEnter(', 'private sealed class CommandEntry') | ForEach-Object {
    $start = $explorer.IndexOf($_)
    if ($start -lt 0) { throw "Missing explorer member: $_" }
    $body = $explorer.IndexOf('{', $start); $depth = 1; $end = $body + 1
    while ($depth -gt 0) { if ($explorer[$end] -eq '{') { $depth++ }; if ($explorer[$end] -eq '}') { $depth-- }; $end++ }
    $explorer.Substring($start, $end - $start)
}
$explorerChecks = (Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/StructureExplorerChecks.cs')).Replace('EXPLORER_MEMBERS', ($explorerMembers -join "`n"))
$sources += Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomNameAdapter.cs')
$sources += Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomBoundaryAdapter.cs')
Add-Type -TypeDefinition ($adapters + "`n" + $extra + "`n" + $structures + "`n" + $extensions + "`n" + $explorerChecks + "`n" + ($sources -join "`n")) -Language CSharp
[ParalivesAccess.Handlers.BuildEditingChecks]::Run()
[ParalivesAccess.Handlers.StructureEditingChecks]::Run()
[ParalivesAccess.Handlers.StructureExplorerChecks]::Run()
[ParalivesAccess.Handlers.RoomExtensionChecks]::Run()
