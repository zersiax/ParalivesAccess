$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
function Read-Method([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature)
    if ($start -lt 0) { throw "Missing production method: $signature" }
    $body = $source.IndexOf('{', $start); $depth = 1; $end = $body + 1
    while ($depth -gt 0) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    $source.Substring($start, $end - $start)
}
$sources = @('PlacementGrid', 'PlacementGridReader', 'SpatialOutline', 'BuildCameraHandler') | ForEach-Object {
    (Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot "src/Handlers/$_.cs")) -replace '(?m)^using [^;]+;\r?\n', ''
}
$window = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/WindowHandler.cs')
$floating = @('public static void FloatingTextPostfix(', 'private static bool IsOwnedWallReadout(',
    'private static bool IsBuildHud(', 'private static bool IsBuildMode(', 'private static void AnnounceBuildMode(') |
    ForEach-Object { Read-Method $window $_ }
$explorer = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/LotObjectHandler.cs')
$explorerMethods = @('private static bool IsTownMapBlockingExplorer(', 'private static bool IsContextStillValid(', 'private static bool HandleBuildShortcut(') |
    ForEach-Object { Read-Method $explorer $_ }
$activity = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/ActivityQueueSummaryHandler.cs')
$activityMethods = @('internal static void BeforeGameSystems(', 'internal static void Tick(', 'private static void ObserveLoadAndSelection(',
    'private static void ScheduleSnapshot(', 'private static void SpeakOnDemand(', 'private static void SpeakPendingSnapshot(',
    'private static Player GetPlayer(', 'private static void ResetForLoading(') | ForEach-Object { Read-Method $activity $_ }
# Exercise the real speech postfix, camera reader/key handler, and activity delivery lifecycle.
# Native observations are adapters; this does not simulate Unity raycasts or audible playback.
$adapters = @'
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using ParalivesAccess.Speech;
namespace UnityEngine {
 public struct Vector2 {public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}}
 public struct Vector3 {public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}}
 public class Transform {public Vector3 position;}
 public static class Time {public static int frameCount;public static float unscaledTime;}
}
namespace UnityEngine.InputSystem {public enum Key {None,F6,F11}public class Keyboard {public static Keyboard current=new Keyboard();}}
public enum State {Game,Loading}
public enum GameStates {LiveMode,BuildMode,PlaceSegment,Painting,CharacterCreator,Terrain,Bulldozer,Pipette,Molding,ReplaceWallWithFence,Photo,PrefabEditor}
public static class GameLoadingManager {public static State State=State.Game;}
public class Player {public GameStates State=GameStates.BuildMode;public int PlayerIndex,Layer;public bool PlayerActionBlockedByUIWindow,IsInSelectHouseholdTutorial,ShowUI=true;public ulong TownMapForFastTravelMethod;public object ItemInPlacement,SegmentInPlacement;public ulong Selected=42;public int GetLotLayer(ulong lot)=>Layer;public ulong GetSelectedCharacterGUID()=>Selected;
 public bool IsInModeAllowingUIBuildModeModes()=>new[]{GameStates.PlaceSegment,GameStates.Bulldozer,GameStates.Pipette,GameStates.Terrain,GameStates.Painting,GameStates.Molding,GameStates.ReplaceWallWithFence}.Contains(State);}
public class HybridCameraFree {public Transform LookTarget=new Transform{position=new Vector3(30,0,30)};}
public class HybridCamera {public HybridCameraFree FreeCamera=new HybridCameraFree();}
public class HybridPlayer {public Player Player=new Player();public HybridCamera HybridCamera=new HybridCamera();public bool HasInputFieldFocused,TownMap;public bool IsInTownMap()=>TownMap;}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public class UIWindow {public bool IsVisible=true;public HybridPlayer PlayerOwner=PlayerManager.Instance.HybridPlayer1;}
public static class UI {public static UIBuildModeCatalog Catalog=new UIBuildModeCatalog();public static T GetOrNull<T>(int player) where T:class=>Catalog as T;}
public class UICharacters:UIWindow {}public class UIKeyBindingTips:UIWindow {}public class UIBuildModeCatalog:UIWindow {}public class UIBuildModeModes:UIWindow {}public class UIPrompt:UIWindow {}
public class AssetLotGridData {public Vector2 OffsetFromOrigin;public float Rotation,CellDimension=1;}
public class AssetLot {public AssetLotGridData Grid=new AssetLotGridData();public AssetLotGridData GetGridData()=>Grid;}
public class LotManager {public static LotManager Instance=new LotManager();public AssetLot Lot=new AssetLot();public AssetLot GetLotByGUID(ulong id)=>Lot;}
public class SegmentObject {public bool IsCurved;public List<Vector3> SegmentPoints;}
public class ZoneSegment {public SegmentObject Segment;public bool FromAToB=true;}
public class ZoneObject {public bool Inside=true;public virtual List<ZoneSegment> Segments {get;set;}=new List<ZoneSegment>();}
public class ZoneWallObject:ZoneObject {}
public class LotPerimeterZoneObject:ZoneObject {public List<Vector3> Points=new List<Vector3>{new Vector3(0,0,0),new Vector3(60,0,0),new Vector3(60,0,60),new Vector3(0,0,60)};public List<Vector3> GetPoints()=>Points;public override List<ZoneSegment> Segments=>Points.Select((p,i)=>new ZoneSegment{Segment=new SegmentObject{SegmentPoints=new List<Vector3>{p,Points[(i+1)%Points.Count]}}}).ToList();}
public class ZoneManager {public static ZoneManager Instance=new ZoneManager();public LotPerimeterZoneObject Perimeter=new LotPerimeterZoneObject();public LotPerimeterZoneObject GetLotPerimeterZoneObjectByGUID(ulong id)=>Perimeter;}
public static class ZoneGeometry {public static bool Contains(ZoneObject zone,Vector3 p)=>zone.Inside;}
public class CubeTransform {public Vector3 FinalSize;}
public class ItemObjectRoot {public CubeTransform CubeTransform;}
public static class MeasurementUnitService {public static string FormatLength(float metres,string format="0.0")=>metres.ToString(format)+" m";}
namespace Setting {public class FloatingText {public ulong ProfileMeasuringLine=1,ProfileMoneyLabelGreen=2,ProfileMoneyLabelRed=3,ProfileError=4,ProfileAngleWidgetText=7;}}
public static class Settings {public static Setting.FloatingText Profiles=new Setting.FloatingText();public static T Get<T>() where T:class=>Profiles as T;}
public class AssetCharacter {public object Data=new object();}
public class CharacterManager {public static CharacterManager Instance=new CharacterManager();public AssetCharacter GetCharacterByGUID(ulong id)=>new AssetCharacter();}
namespace ParalivesAccess {
 public static class Loc {public static string Get(string key,params object[] args)=>key+"("+string.Join(";",args)+")";}
 public class Logger {public List<string> Lines=new List<string>();public void LogInfo(string s){Lines.Add(s);}public void LogError(string s){throw new Exception(s);}}
 public static class Plugin {public static Logger Log=new Logger();}
}
namespace ParalivesAccess.Speech {
 public static class ScreenReader {public static List<string> Said=new List<string>();public static void Speak(string text,bool interrupt=true,string diagnosticSource=null){Said.Add(text);}public static string NormalizeForOutput(string s)=>s;}
 public static class AnnouncementCoordinator {public static void DiscardPending(string key){}public static void PublishLatestImportant(string key,string text){ScreenReader.Speak(text);}}
}
namespace ParalivesAccess.Handlers {
 public static class ModInputKey {public static bool Pressed,Available=true;public static Key PressedKey=Key.None;public static bool WasPressed(Keyboard k,Key key)=>PressedKey==Key.None?Pressed:PressedKey==key;public static bool IsAvailable(HybridPlayer h,Key key,out string conflict){conflict="binding conflict";return Available;}}
 public static class BuildCatalogHandler {public static bool Enabled=true;public static Key OpenKey=Key.F6;public static int Opens;public static void Open(HybridPlayer h){Opens++;}}
 public static class ContextKeyInput {public static bool ContextPressed;}
 public static class SettingsHandler {public static bool IsCapturingBindingInput;}
 public static class CoordinateEntryHandler {public static bool BlocksPlacement;}
 public static class LotBuildInputOwnership {public static bool OwnsInput;}
 public static class TownMapLotHandler {public static bool Enabled=true,OwnsInput;}
 public static class ItemPlacementHandler {public static ulong Lot=1;public static ulong ResolvePlacementLot(Player p,out string source){source="camera";return Lot;}}
 public static class WallConstructionHandler {public static bool OwnsInput;}
 public static class RoomSpatialReader {public static List<ZoneWallObject> Rooms=new List<ZoneWallObject>();public static List<ZoneWallObject> OrderedRooms(ulong lot,int layer)=>Rooms;internal static string Region(float v,string a,string b,string c)=>Loc.Get(v<1f/3?a:v>2f/3?c:b);}
 public static class ActivityChecks {
  private const float LoadSettleSeconds=2,SelectionSettleSeconds=0.65f;
  private static bool _contextRequested,_wasInGame;private static int _contextRequestFrame=-1;
  private static ulong _selectedCharacterGuid,_pendingSnapshotGuid;private static float _snapshotReadyAt;
  private static bool TryBuildSummary(Player player,bool includeNoSelection,out string summary,out ulong guid){summary="activity snapshot";guid=player.Selected;return true;}
  ACTIVITY_METHODS
 }
 public static class FeedbackChecks {
  public static bool AnnounceWindows=true;
  private static bool _buildSessionAnnounced;
  FLOATING_METHODS
  static int checks;
  static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
  static bool Said(string value)=>ScreenReader.Said.Exists(s=>s.Contains(value));
  public static string Run(){
   WallConstructionHandler.OwnsInput=true;
   for(int frame=0;frame<600;frame++){FloatingTextPostfix(1,"8 m");FloatingTextPostfix(2,"96");FloatingTextPostfix(3,"96");FloatingTextPostfix(7,"90 degrees");}
   Check(ScreenReader.Said.Count==0&&Plugin.Log.Lines.Count==0,"continuous owned preview labels produce neither speech nor per-frame log flood");
   FloatingTextPostfix(4,"Not enough money");FloatingTextPostfix(5,"Game saved");FloatingTextPostfix(6,"Money spent");
   Check(ScreenReader.Said.Count==3&&Said("Not enough money")&&Said("Game saved")&&Said("Money spent"),"errors, saved feedback and actual transactions remain spoken during construction");
   WallConstructionHandler.OwnsInput=false;FloatingTextPostfix(1,"8 m");Check(Said("8 m"),"unsupported/native-only tools retain their floating feedback");
   FloatingTextPostfix(7,"135 degrees");Check(Said("135 degrees"),"native-only angle markers remain audible without accessible wall ownership");
   WallConstructionHandler.OwnsInput=true;Settings.Profiles.ProfileError=2;FloatingTextPostfix(2,"Error wins");Check(Said("Error wins"),"error profile always wins over preview filtering");Settings.Profiles.ProfileError=4;
   int before=ScreenReader.Said.Count;AnnounceWindows=false;FloatingTextPostfix(4,"disabled");Check(ScreenReader.Said.Count==before,"window-announcement preference still applies");AnnounceWindows=true;
   FloatingTextPostfix(0,"unknown profile");Check(Said("unknown profile"),"unset or unknown profile is never silently suppressed");
   var h=PlayerManager.Instance.HybridPlayer1;
   ScreenReader.Said.Clear();
   h.Player.State=GameStates.LiveMode;AnnounceBuildMode(h,false);h.TownMap=true;
   h.Player.State=GameStates.BuildMode;AnnounceBuildMode(h,false);
   Check(ScreenReader.Said.Count==1&&Said("build_mode_map_intro"),"Tab at map zoom announces build mode without a visible catalogue");
   for(int frame=0;frame<600;frame++)AnnounceBuildMode(h,false);
   Check(ScreenReader.Said.Count==1,"staying in build mode never repeats the entry announcement");
   foreach(var mode in new[]{GameStates.PlaceSegment,GameStates.Painting,GameStates.Terrain,GameStates.Bulldozer,GameStates.Pipette,GameStates.Molding,GameStates.ReplaceWallWithFence}){
    h.Player.State=mode;AnnounceBuildMode(h,false);Check(IsBuildHud(new UIBuildModeModes()),"native build tool belongs to the same session: "+mode);
   }
   h.Player.State=GameStates.BuildMode;h.TownMap=false;AnnounceBuildMode(h,false);
   Check(ScreenReader.Said.Count==1,"returning from a tool or zooming back to the catalogue does not repeat the intro");
   h.Player.State=GameStates.LiveMode;AnnounceBuildMode(h,false);h.Player.State=GameStates.BuildMode;AnnounceBuildMode(h,false);
   Check(ScreenReader.Said.Count==2&&Said("build_mode_intro"),"leaving and reentering build mode announces the new session");
   foreach(var mode in new[]{GameStates.CharacterCreator,GameStates.Photo,GameStates.PrefabEditor}){
    ScreenReader.Said.Clear();h.Player.State=mode;AnnounceBuildMode(h,false);Check(ScreenReader.Said.Count==0&&!IsBuildHud(new UICharacters()),"unrelated mode is not called build mode: "+mode);
   }
   h.Player.State=GameStates.BuildMode;h.Player.PlayerActionBlockedByUIWindow=true;AnnounceBuildMode(h,false);
   Check(ScreenReader.Said.Count==0,"modal speech retains priority over a pending build introduction");
   h.Player.PlayerActionBlockedByUIWindow=false;AnnounceBuildMode(h,false);Check(ScreenReader.Said.Count==1,"entry is announced after a modal closes");
   GameLoadingManager.State=State.Loading;ScreenReader.Said.Clear();AnnounceBuildMode(h,false);
   Check(ScreenReader.Said.Count==0,"loading clears the session without speaking stale build state");
   GameLoadingManager.State=State.Game;AnnounceBuildMode(h,false);Check(ScreenReader.Said.Count==1,"reloaded build state gets a fresh entry announcement");
   ScreenReader.Said.Clear();h.Player.State=GameStates.LiveMode;AnnounceBuildMode(h,false);h.Player.State=GameStates.BuildMode;AnnounceBuildMode(h,true);AnnounceBuildMode(h,false);
   Check(ScreenReader.Said.Count==0,"an explicit F3 report replaces the entry hint without a delayed duplicate");
   foreach(int owner in new[]{0,1,2,3}){
    h.Player.State=GameStates.LiveMode;AnnounceBuildMode(h,false);h.Player.State=GameStates.BuildMode;
    LotBuildInputOwnership.OwnsInput=owner==0;CoordinateEntryHandler.BlocksPlacement=owner==1;TownMapLotHandler.OwnsInput=owner==2;h.Player.ItemInPlacement=owner==3?new object():null;
    AnnounceBuildMode(h,false);LotBuildInputOwnership.OwnsInput=CoordinateEntryHandler.BlocksPlacement=TownMapLotHandler.OwnsInput=false;h.Player.ItemInPlacement=null;AnnounceBuildMode(h,false);
    Check(ScreenReader.Said.Count==0,"active workflow suppresses entry hint and later repeats: "+owner);
   }
   h.Player.State=GameStates.LiveMode;AnnounceBuildMode(h,false);h.Player.State=GameStates.BuildMode;AnnounceWindows=false;AnnounceBuildMode(h,false);AnnounceWindows=true;AnnounceBuildMode(h,false);
   Check(ScreenReader.Said.Count==0,"disabled window announcements do not leak a delayed build intro");
   h.Player.State=GameStates.LiveMode;AnnounceBuildMode(h,false);h.Player.State=GameStates.BuildMode;h.HasInputFieldFocused=true;AnnounceBuildMode(h,false);h.HasInputFieldFocused=false;
   h.Player.ShowUI=false;AnnounceBuildMode(h,false);h.Player.ShowUI=true;SettingsHandler.IsCapturingBindingInput=true;AnnounceBuildMode(h,false);
   Check(ScreenReader.Said.Count==0,"text entry, hidden HUD and rebinding defer the introductory hint");SettingsHandler.IsCapturingBindingInput=false;AnnounceBuildMode(h,false);
   Check(ScreenReader.Said.Count==1,"hint becomes available after text and binding input finish");
   Check(IsBuildHud(new UIBuildModeModes())&&!IsBuildHud(new UIPrompt()),"build tool banner is quiet without hiding prompts");
   h.Player.State=GameStates.LiveMode;Check(!IsBuildHud(new UICharacters()),"normal household window remains audible in live mode");h.Player.State=GameStates.BuildMode;
   ScreenReader.Said.Clear();
   Check(BuildCameraHandler.TryGetContext(h,out string context)&&context.Contains("build_camera_outdoors")&&context.Contains("layout_near_centre")&&!context.Contains("coordinates")&&!context.Contains("bounds"),"idle F3 reports camera outdoors and centre without coordinate dump");
   h.Player.Layer=2;RoomSpatialReader.Rooms.Add(new ZoneWallObject{Inside=false});RoomSpatialReader.Rooms.Add(new ZoneWallObject());
   Check(BuildCameraHandler.TryGetContext(h,out context)&&context.Contains("build_camera_room(2)")&&context.EndsWith(";2)"),"camera room and active floor come from live native observations");
   RoomSpatialReader.Rooms.Clear();h.Player.Layer=0;h.HybridCamera.FreeCamera.LookTarget.position=new Vector3(3,0,55);
   Check(BuildCameraHandler.TryGetContext(h,out context)&&context.Contains("layout_from_centre")&&context.Contains("layout_back_left"),"moving the camera changes its reported distance and direction");
   ZoneManager.Instance.Perimeter.Inside=false;
   Check(BuildCameraHandler.TryGetContext(h,out context)&&context.Contains("build_outside_lot")&&!context.Contains("outdoors"),"native containment rejects concave cutouts even within rectangular bounds");ZoneManager.Instance.Perimeter.Inside=true;
   ModInputKey.Pressed=true;BuildCameraHandler.Tick();
   Check(Said("build_lot_bounds(0;60;0;60)")&&Said("placement_grid_position(3;55)"),"idle F11 gives live camera coordinates and both bounds without selecting a tool");
   ScreenReader.Said.Clear();ModInputKey.Available=false;BuildCameraHandler.Tick();Check(Said("binding conflict")&&!Said("build_lot_bounds"),"F11 honors a conflicting native binding");ModInputKey.Available=true;
   ScreenReader.Said.Clear();LotBuildInputOwnership.OwnsInput=true;BuildCameraHandler.Tick();
   Check(!BuildCameraHandler.TryGetContext(h,out _)&&ScreenReader.Said.Count==0,"F7, F6 and other exclusive owners retain their controls");LotBuildInputOwnership.OwnsInput=false;
   TownMapLotHandler.OwnsInput=true;BuildCameraHandler.Tick();Check(!BuildCameraHandler.TryGetContext(h,out _)&&ScreenReader.Said.Count==0,"town-map browser keeps context priority when opened from build mode");TownMapLotHandler.OwnsInput=false;
   h.HasInputFieldFocused=true;BuildCameraHandler.Tick();Check(!BuildCameraHandler.TryGetContext(h,out _)&&ScreenReader.Said.Count==0,"text entry takes priority");h.HasInputFieldFocused=false;
   h.Player.PlayerActionBlockedByUIWindow=true;Check(!BuildCameraHandler.TryGetContext(h,out _),"native modal takes priority");h.Player.PlayerActionBlockedByUIWindow=false;
   h.Player.ItemInPlacement=new object();Check(!BuildCameraHandler.TryGetContext(h,out _),"item preview keeps F3 and F11");h.Player.ItemInPlacement=null;
   h.Player.State=GameStates.PlaceSegment;Check(!BuildCameraHandler.TryGetContext(h,out _),"wall tool retains its context and detail key");h.Player.State=GameStates.BuildMode;
   SettingsHandler.IsCapturingBindingInput=true;Check(!BuildCameraHandler.TryGetContext(h,out _),"rebinding takes priority");SettingsHandler.IsCapturingBindingInput=false;
   CoordinateEntryHandler.BlocksPlacement=true;Check(!BuildCameraHandler.TryGetContext(h,out _),"coordinate form and its close frame take priority");CoordinateEntryHandler.BlocksPlacement=false;
   h.HybridCamera.FreeCamera.LookTarget=null;Check(BuildCameraHandler.TryGetContext(h,out context)&&context.Contains("build_camera_unavailable"),"missing camera gives an explicit response");h.HybridCamera.FreeCamera.LookTarget=new Transform();
   ItemPlacementHandler.Lot=0;Check(BuildCameraHandler.TryGetContext(h,out context)&&context.Contains("lot_unavailable"),"unresolved lot cannot invent coordinates");ItemPlacementHandler.Lot=1;
   foreach(double angle in new[]{0,30,90,-45}){
    var native=new PlacementGrid(213,-147,angle,1);var points=new List<Vector3>();
    foreach(var pair in new[]{new[]{0.25,0.5},new[]{40.25,0.5},new[]{40.25,20.5},new[]{0.25,20.5}}){native.World(pair[0],pair[1],out double x,out double z);points.Add(new Vector3((float)x,0,(float)z));}
    ZoneManager.Instance.Perimeter.Points=points;LotManager.Instance.Lot.Grid.OffsetFromOrigin=new Vector2(213,-147);LotManager.Instance.Lot.Grid.Rotation=(float)angle;
    Check(PlacementGridReader.TryRead(1,1,out var grid)&&PlacementGridReader.TryBounds(1,grid,out double x1,out double y1,out double x2,out double y2)&&Math.Abs(x1-0.25)<0.001&&Math.Abs(y1-0.5)<0.001&&Math.Abs(x2-40.25)<0.001&&Math.Abs(y2-20.5)<0.001,"rotated fractional bounds retain actual minimum and maximum "+angle);
    native.World(20.25,10.5,out double cx,out double cz);Check(PlacementGridReader.DescribeRegion(1,grid,new Vector3((float)cx,0,(float)cz)).Contains("centre"),"camera turn does not redefine the lot frame "+angle);
   }
   ZoneManager.Instance.Perimeter.Points[0]=new Vector3(float.NaN,0,0);Check(!PlacementGridReader.TryRead(1,1,out _),"invalid native geometry refuses rather than reporting misleading coordinates");
   ScreenReader.Said.Clear();ContextKeyInput.ContextPressed=true;h.Player.State=GameStates.BuildMode;ActivityChecks.BeforeGameSystems();ActivityChecks.Tick();
   Check(ScreenReader.Said.Count==0,"F3 does not append a Para activity report in build mode");
   h.Player.State=GameStates.LiveMode;ActivityChecks.BeforeGameSystems();ActivityChecks.Tick();Check(Said("activity snapshot"),"live-mode F3 still includes the activity report");
   ScreenReader.Said.Clear();ContextKeyInput.ContextPressed=false;h.Player.Selected=7;ActivityChecks.Tick();h.Player.State=GameStates.BuildMode;Time.unscaledTime=100;ActivityChecks.Tick();Check(ScreenReader.Said.Count==0,"pending live activity snapshot cannot interrupt building");
   GameLoadingManager.State=State.Loading;Check(!BuildCameraHandler.TryGetContext(h,out _),"loading does not read stale camera state");
   return checks+" build feedback checks passed; native speech and camera movement still require gameplay verification.";
  }
 }
}
'@
$adapters = $adapters.Replace('FLOATING_METHODS', ($floating -join "`n")).Replace('ACTIVITY_METHODS', ($activityMethods -join "`n"))
$sources += (Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/BuildModeExplorerChecks.cs')).Replace('EXPLORER_METHODS', ($explorerMethods -join "`n"))
$sources += Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomNameAdapter.cs')
Add-Type -TypeDefinition ($adapters + "`n" + ($sources -join "`n")) -Language CSharp
[ParalivesAccess.Handlers.FeedbackChecks]::Run()
[ParalivesAccess.Handlers.BuildModeExplorerChecks]::Run()
