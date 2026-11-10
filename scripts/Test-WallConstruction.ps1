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
$windowSource = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/WindowHandler.cs')
$floatingMethods = @('public static void FloatingTextPostfix(', 'private static bool IsOwnedWallReadout(') |
    ForEach-Object { Read-Method $windowSource $_ }
# Compile the complete production controller, plan, grid reader and ownership gate. The adapters
# supply observations, never reproduce the game's wall geometry, purchases, snapping or undo.
$sources = @('PlacementGrid', 'PlacementPointer', 'PlacementGridReader', 'ConstructionGeometry', 'RectangleWallPlan', 'SharedWallBoundary', 'WallConstructionHandler', 'LotBuildInputOwnership', 'LotDescriptionHandler') | ForEach-Object {
    (Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot "src/Handlers/$_.cs")) -replace '(?m)^using [^;]+;\r?\n', ''
}
$adapters = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using HarmonyLib;
using Setting;
using ParalivesAccess.Speech;
namespace UnityEngine {
 public static class Time {public static int frameCount;public static float unscaledTime;}
 public class GameObject {public bool activeInHierarchy=true;}
 public struct Vector2 {public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}public static float Distance(Vector2 a,Vector2 b)=>(float)Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y));}
 public struct Vector3 {public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}public static Vector3 zero=>default;public static float Distance(Vector3 a,Vector3 b)=>(float)Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y)+(a.z-b.z)*(a.z-b.z));}
 public class Transform {public Vector3 position;}
}
namespace UnityEngine.InputSystem {
 public enum Key {None,J,R,Home,F6,F7,F10,F11}
 public class KeyState {public bool wasPressedThisFrame,wasReleasedThisFrame,isPressed;}
 public class Keyboard {public static Keyboard current=new Keyboard();public KeyState leftCtrlKey=new KeyState(),rightCtrlKey=new KeyState(),leftAltKey=new KeyState(),rightAltKey=new KeyState(),leftMetaKey=new KeyState(),rightMetaKey=new KeyState(),rightArrowKey=new KeyState(),leftArrowKey=new KeyState(),upArrowKey=new KeyState(),downArrowKey=new KeyState(),enterKey=new KeyState(),numpadEnterKey=new KeyState(),leftShiftKey=new KeyState(),rightShiftKey=new KeyState(),homeKey=new KeyState();public Key Pressed;}
 public class PointerControl {public Vector2 Value;public Vector2 ReadValue()=>Value;}
 public class Mouse {public static Mouse current=new Mouse();public PointerControl position=new PointerControl();public Vector2 Requested;public void WarpCursorPosition(Vector2 v){Requested=v;}public void Arrive(){InputManager.Instance.Cursor=Requested;position.Value=Requested;}}
}
namespace HarmonyLib {
 public class Patch {public string owner="test";public MethodInfo PatchMethod;}
 public class Patches {public List<Patch> Prefixes=new List<Patch>(),Postfixes=new List<Patch>(),Finalizers=new List<Patch>();}
 public class HarmonyMethod {public MethodInfo Method;public HarmonyMethod(MethodInfo m){Method=m;}}
 public class Harmony {public static Dictionary<MethodInfo,Patches> All=new Dictionary<MethodInfo,Patches>();public void Patch(MethodInfo m,HarmonyMethod prefix=null,HarmonyMethod postfix=null,HarmonyMethod finalizer=null){var p=new Patches();if(prefix!=null)p.Prefixes.Add(new Patch{PatchMethod=prefix.Method});if(postfix!=null)p.Postfixes.Add(new Patch{PatchMethod=postfix.Method});if(finalizer!=null)p.Finalizers.Add(new Patch{PatchMethod=finalizer.Method});All[m]=p;}public static Patches GetPatchInfo(MethodInfo m)=>All.ContainsKey(m)?All[m]:null;}
 public static class AccessTools {public static MethodInfo Method(Type t,string n,Type[] p=null)=>p==null?t.GetMethod(n,BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic):t.GetMethod(n,BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,p,null);}
}
namespace Setting {public class Gameplay {public bool UsesWallGrid=true;}public class WallPlacementGrid {public float ShiftCellDimensionMultiplier=0.5f;}public class Widgets {public WidgetProfile WidgetNodeCursor=new WidgetProfile(),WidgetAngleCurve=new WidgetProfile();}public class FloatingText {public ulong ProfileMeasuringLine=1,ProfileMoneyLabelGreen=2,ProfileMoneyLabelRed=3,ProfileError=4,ProfileAngleWidgetText=5;}}
public class WidgetProfile {public ulong GUID=1;}
public static class Settings {static Dictionary<Type,object> all=new Dictionary<Type,object>();public static T Get<T>() where T:new(){if(!all.ContainsKey(typeof(T)))all[typeof(T)]=new T();return (T)all[typeof(T)];}}
public enum GameStates {PlaceSegment,BuildMode,LiveMode,Painting}
public enum State {Game}
public static class GameLoadingManager {public static State State=State.Game;}
public enum SegmentTypes {Wall,LotPerimeter}
public enum WallHeightType {FullWall,HalfWall,Fence}
public enum WallPlacementMode {Straight,Curved}
public class NodeObject {public Vector3 Position;}
public class NodeCursorData {public bool PositionIllegal;}
public class SegmentObject {public GameObject gameObject=new GameObject();public int InstanceID,Layer,Cluster,PlacementPlayer=-1,Price=60;public ulong LotPlacedOnGUID=1;public bool IsCurved;public NodeObject NodeA=new NodeObject(),NodeB=new NodeObject();public float Length=3;public int GetPrice()=>Price;}
public class SegmentWallObject:SegmentObject {public int Height=6;public List<SegmentWallSectionObject> WallSections=new List<SegmentWallSectionObject>{new SegmentWallSectionObject()};}
public class SegmentWallSectionObject {public GameObject gameObject=new GameObject();public int InstanceID=4,StartHeight,EndHeight=6;public bool IsFence,DisabledByUser,DisabledButVisible;}
public class SegmentManager {public static SegmentManager Instance=new SegmentManager();public List<SegmentObject> Walls=new List<SegmentObject>();public List<SegmentObject> GetSegmentsInOwnLots()=>Walls;public List<SegmentObject> GetLotSegments(ulong lot)=>Walls;}
public class Player {public int CurrentFullWallHeight=6;public int PlayerIndex,CurrentHoveredFloorLayer,Layer;public bool FreezeCameraMovement,PlayerActionBlockedByUIWindow,CatalogueOpened,StartNewSegment,StartNewSegmentNextFrame,PlayerActionsAreAllowed=true;public GameStates State=GameStates.PlaceSegment;public SegmentTypes CurrentSegmentType=SegmentTypes.Wall;public WallHeightType CurrentWallHeightType=WallHeightType.FullWall;public WallPlacementMode WallPlacementMode=WallPlacementMode.Straight;public SegmentObject SegmentInPlacement;public NodeCursorData NodeCursor=new NodeCursorData();public int GetLotLayer(ulong lot)=>Layer;}
public class HybridCameraFree {public float CurrentExtraLookUpRatio,TargetDistanceSpeed;public Transform LookTarget=new Transform();}
public class HybridCamera {public HybridCameraFree FreeCamera=new HybridCameraFree();}
public class Button {public bool Down,Up,Pressed;public int Count;public void SetValue(float v){Down=v>0.5f;Up=!Down;Pressed=false;if(Down)Count++;}public void Update(){if(Down){Down=false;Pressed=true;}if(Up){Up=false;Pressed=false;}}}
public class HybridPlayer {public Player Player=new Player();public HybridCamera HybridCamera=new HybridCamera();public bool HasInputFieldFocused,IsUsingGamePad,ForceTriggerCancelEscapeBackWithRightClick;public Button ButtonCancel=new Button(),ButtonUndo=new Button(),ButtonConfirm=new Button();}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public class InputManager {public static InputManager Instance=new InputManager();public static bool ControlOrCommand,Alt;public Vector2 Cursor;public bool OverUI;public Vector2 GetCursorPosition(int player)=>Cursor;public bool IsPointerOverUI(int player)=>OverUI;}
public class WidgetVisual {public Transform Transform=new Transform();}
public class NodeAngleWidget:WidgetVisual {public int SignedAngle;}
public static class FormatService {public static string GetAngle(int value)=>value+" degrees";}
public class WidgetManager {public static WidgetManager Instance=new WidgetManager();public WidgetVisual Cursor=new WidgetVisual();public List<WidgetVisual> Angles=new List<WidgetVisual>();public WidgetVisual GetWidget(int player,ulong guid)=>Cursor;public List<WidgetVisual> GetWidgets(int player,ulong guid)=>Angles;}
public class AssetLotGridData {public Vector2 OffsetFromOrigin;public float Rotation,CellDimension=1;}
public class AssetLot {public AssetLotGridData Grid=new AssetLotGridData();public AssetLotGridData GetGridData()=>Grid;}
public class LotManager {public static LotManager Instance=new LotManager();public AssetLot Lot=new AssetLot();public AssetLot GetLotByGUID(ulong id)=>Lot;}
public class LotPerimeterZoneObject {public Vector3 AveragePosition;public List<int> Segments=new List<int>{1,2,3,4};public List<Vector3> GetPoints()=>new List<Vector3>{new Vector3(-10,0,-10),new Vector3(10,0,-10),new Vector3(10,0,10),new Vector3(-10,0,10)};}
public class ZoneSegment {public SegmentObject Segment;}
public class ZoneObject {public int InstanceID,Layer;public float SurfaceArea;public List<ZoneSegment> Segments=new List<ZoneSegment>();}
public class ZoneWallObject:ZoneObject {}
public class ZoneManager {public static ZoneManager Instance=new ZoneManager();public List<ZoneObject> Zones=new List<ZoneObject>();public HashSet<ZoneWallObject> DirtyWallZones=new HashSet<ZoneWallObject>();public LotPerimeterZoneObject GetLotPerimeterZoneObjectByGUID(ulong id)=>new LotPerimeterZoneObject();public List<ZoneObject> GetLotZones(ulong lot)=>Zones;public (bool,ulong) GetNodeCursorLotPerimeterOfHousehold(Vector3 p,ulong lot)=>(Math.Abs(p.x)<10&&Math.Abs(p.z)<10,1);}
public class WallHeightManager {public static WallHeightManager Instance=new WallHeightManager();public float GetFloorWorldY(ulong lot,int layer)=>layer*3;}
public static class BuildModeRefreshManager {public static bool IsZoneStateRefreshed=true;}
public class UIBuildModeModes {public void CloseClicked(){PlayerManager.Instance.HybridPlayer1.Player.State=GameStates.BuildMode;}}
public static class UI {public static T GetOrNull<T>(int index)where T:class=>new UIBuildModeModes() as T;}
public class ItemObjectRoot {public ItemCubeTransform CubeTransform=new ItemCubeTransform();}
public class ItemCubeTransform {public Vector3 FinalSize;}
public class MeasurementUnit {public float MetricMultiplier=1;public string ShortName="m";}
public static class MeasurementUnitService {public static MeasurementUnit Unit=new MeasurementUnit();public static MeasurementUnit GetCurrentUnit()=>Unit;public static string FormatLength(float n,string format)=>n.ToString(format)+" m";public static string FormatSurfaceArea(float n)=>n+" square metres";}
public class StartCreateSegment {public void UpdateForPlayer(Player player){}}
public class CheckToReplaceSegment {public void UpdateForPlayer(Player player){}}
public class EndCreateSegment {public void UpdateForPlayer(Player player){}private void CompletePlaceSegment(Player player,int price,SegmentObject segmentSnappedToCursor){}}
namespace ParalivesAccess.Speech {public static class ScreenReader {public static List<string> Spoken=new List<string>();public static void Speak(string s){Spoken.Add(s);}public static string NormalizeForOutput(string s)=>s;}}
namespace ParalivesAccess {
 public class Logger {public void LogInfo(string s){}public void LogError(string s){throw new Exception(s);}}
 public static class Plugin {public const string PluginGuid="test";public static Logger Log=new Logger();}
 public static class Loc {public static string Get(string k,params object[] v)=>k+"("+string.Join(";",v)+")";}
}
namespace ParalivesAccess.Handlers { public static class LotCursorHandler {public static bool OwnsInput=>false;}
 public static class SettingsHandler {public static bool IsCapturingBindingInput;}
 public static class TownMapLotHandler {public static bool Enabled=true,OwnsInput;}
 public static class LotLayoutReader {public static string Describe(HybridPlayer h,bool wholeLot)=>wholeLot?"whole lot description":"current room description";}
 internal sealed class RoomExtensionTarget {internal ulong Lot=1;internal int Layer=0;internal bool IsLive=true;}
 internal sealed class RoomExtensionReview {internal RoomExtensionTarget Target=new RoomExtensionTarget();internal RectangleWallPlan Plan;internal SharedWallBoundary Shared;internal int Height=6;internal string Description="extension review",Failure;internal string Problem()=>Failure;}
 internal static class RoomExtensionReader {internal static bool SelectWallTool(HybridPlayer h){h.Player.State=GameStates.PlaceSegment;return true;}}
 internal static class StructureEditingHandler {internal static bool Context(Player p,ulong lot,int layer)=>p.State==GameStates.BuildMode&&p.Layer==layer&&!p.PlayerActionBlockedByUIWindow;}
 internal static class WallFaceReader {internal static bool Usable(SegmentWallObject wall,ulong lot,int layer)=>wall!=null&&wall.gameObject.activeInHierarchy&&wall.LotPlacedOnGUID==lot&&wall.Layer==layer&&wall.PlacementPlayer==-1&&!wall.IsCurved;}
 public static class CoordinateEntryHandler {public static bool BlocksPlacement,Requested;public static Action<double,double> Apply;public static Func<bool> Valid;public static double First,Second;public static string Hint;public static bool WasRequested(HybridPlayer h)=>Requested;public static void Open(HybridPlayer h,object preview,string hint,double x,double z,Func<bool> valid,Action<double,double> apply,Key openingKey=Key.F10,string title=null,string instructions=null,bool announceReview=true){Valid=valid;Apply=apply;First=x;Second=z;Hint=hint;}}
 public static class BuildCameraHandler {public static string DescribeCamera(HybridPlayer h,bool detailed)=>"Camera focus: outdoors, central part of the lot";}
 public static class FloorCoveringHandler {public static bool OwnsInput;}
 public static class ModInputKey {public static bool Available=true;public static bool WasPressed(Keyboard k,Key key)=>k!=null&&key!=Key.None&&k.Pressed==key;public static bool IsAvailable(HybridPlayer p,Key k,out string conflict){conflict="hotkey_conflict";return Available;}}
 public static class BuildCatalogHandler {public static Key OpenKey=Key.F6;public static bool OwnsInput;}
 public static class TutorialHandler {public static bool OwnsInput;}
 public static class ContextActionHandler {public static bool OwnsInput;}
 public static class OccupationHandler {public static bool OwnsInput;}
 public static class LotObjectHandler {public static Key OpenKey=Key.F7;public static bool OwnsInput;}
 public static class ItemPlacementHandler {public static bool OwnsInput,Visible=true;public static ulong Lot=1;public static ulong ResolvePlacementLot(Player p,out string source){source="camera";return Lot;}public static bool TryProjectTarget(HybridPlayer p,Vector3 target,out Vector2 screen){screen=new Vector2(500+target.x*10,300+target.z*10);return Visible;}}
 public static class WorldTargeting {public static int Calls;public static bool FrameArea(Player p,ulong lot,int layer,Vector3 target,string source){Calls++;ItemPlacementHandler.Visible=true;return true;}}
 public static class WallChecks {
  static int checks;
  static Type Handler=>typeof(WallConstructionHandler);
  static object Get(string name)=>Handler.GetField(name,BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
  static object Call(string name,params object[] args)=>Handler.GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);
  static void Check(bool ok,string name){if(!ok)throw new Exception(name+"; spoken ("+ScreenReader.Spoken.Count+"): "+string.Join(" | ",ScreenReader.Spoken.Take(12)));checks++;}
  static bool Said(string prefix)=>ScreenReader.Spoken.Any(s=>s.StartsWith(prefix));
  static HybridPlayer H=>PlayerManager.Instance.HybridPlayer1;
  static Vector3 Target=>(Vector3)Get("_target");
  static string Stage=>Get("_guide").ToString();
  static void Reset(){
   CoordinateEntryHandler.BlocksPlacement=CoordinateEntryHandler.Requested=false;CoordinateEntryHandler.Apply=null;
   Call("Release",false);PlayerManager.Instance=new PlayerManager();WidgetManager.Instance=new WidgetManager();InputManager.Instance=new InputManager();ZoneManager.Instance=new ZoneManager();LotManager.Instance=new LotManager();SegmentManager.Instance=new SegmentManager();Mouse.current=new Mouse();Keyboard.current=new Keyboard();
   Settings.Get<Gameplay>().UsesWallGrid=true;InputManager.ControlOrCommand=InputManager.Alt=false;BuildModeRefreshManager.IsZoneStateRefreshed=true;ModInputKey.Available=true;ItemPlacementHandler.Visible=true;ItemPlacementHandler.Lot=1;WallConstructionHandler.Enabled=true;
   BuildCatalogHandler.OwnsInput=TutorialHandler.OwnsInput=ContextActionHandler.OwnsInput=OccupationHandler.OwnsInput=LotObjectHandler.OwnsInput=false;
   WorldTargeting.Calls=0;ScreenReader.Spoken.Clear();Time.frameCount++;
  }
  static void End(bool arrive=true){
   if(arrive){Mouse.current.Arrive();WidgetManager.Instance.Cursor.Transform.position=Target;if(H.Player.SegmentInPlacement!=null)H.Player.SegmentInPlacement.NodeB.Position=new Vector3(Target.x,0,Target.z);}
   Call("AfterNativeConfirm",H.Player);
   WallConstructionHandler.AfterGameSystems();LotDescriptionHandler.Tick();Keyboard.current=new Keyboard();H.ButtonConfirm.Down=H.ButtonCancel.Down=H.ButtonUndo.Down=false;Time.frameCount++;Time.unscaledTime+=0.02f;
  }
  static void Tick(bool arrive=true){WallConstructionHandler.BeforeGameSystems();End(arrive);}
  static void Settle(){for(int i=0;i<4;i++)Tick();}
  static void Enter(){Keyboard.current.enterKey.wasReleasedThisFrame=true;WallConstructionHandler.BeforeGameSystems();if((bool)Get("_pendingStart"))Call("BeforeNativeStart",H.Player);else if((int)Get("_pendingId")>=0)Call("BeforeNativeReplacement",H.Player);}
  static void StartObserved(int id){H.Player.SegmentInPlacement=new SegmentObject{InstanceID=id,NodeA=new NodeObject{Position=Target},NodeB=new NodeObject{Position=Target}};End();}
  static void CommitObserved(){
   var args=new object[]{H.Player,0};Call("BeforeComplete",args);int captured=(int)args[1];Check(captured>=0,"completion captures exact pending instance");
   H.Player.SegmentInPlacement=null;Call("AfterComplete",H.Player,60,captured);End();
  }
  static RectangleWallPlan Plan=>(RectangleWallPlan)Get("_plan");
  static void RequestRectangle(bool shared=false){
   Settle();Keyboard.current.Pressed=Key.R;Keyboard.current.leftShiftKey.isPressed=true;Tick();Enter();End();
   Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Tick();Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Tick();
   Keyboard.current.upArrowKey.wasPressedThisFrame=true;Tick();Keyboard.current.upArrowKey.wasPressedThisFrame=true;Tick();
   Settle();Enter();End();Check(Stage==(shared?"ReviewingSharedEdge":"StartingEdge")&&Plan!=null,"two corners create a guide without a native purchase");
  }
  static void CompleteEdges(int count){
   for(int i=0;i<count;i++){
    for(int n=0;n<5&&!(bool)Get("_pendingStart");n++){WallConstructionHandler.BeforeGameSystems();if(!(bool)Get("_pendingStart"))End();}
    if(!(bool)Get("_pendingStart"))throw new Exception("expected native start request");
    Call("BeforeNativeStart",H.Player);StartObserved(300+i);Settle();Enter();CommitObserved();
   }
  }
  public static string Run(){
   Check(WallConstructionHandler.ApplyPatches(new Harmony()),"completion, all native timing guards and cleanup finalizers installed and verified");
   Check(ConstructionGeometry.Overlaps(0,0,4,0,4,0,0,0),"opposite-direction replacement refused");
   Check(ConstructionGeometry.Overlaps(0,0,4,0,1,0,2,0),"contained wall overlap refused");
   Check(ConstructionGeometry.Overlaps(0,0,2,0,1,0,4,0),"partial wall overlap refused");
   Check(!ConstructionGeometry.Overlaps(0,0,2,0,2,0,4,0),"ordinary shared endpoint allowed");
   Check(!ConstructionGeometry.Overlaps(0,0,2,0,1,-1,1,1),"perpendicular crossing delegated to game");
   Check(!ConstructionGeometry.Overlaps(0,0,2,0,0,1,2,1),"parallel separate walls allowed");
   foreach(double angle in new[]{0,30,90,-45}){
    var g=new PlacementGrid(123,-456,angle,1);g.World(2,3,out double x1,out double z1);g.World(6,8,out double x2,out double z2);
    Check(RectangleWallPlan.TryCreateSized(g,x1,z1,7.7,5.6,out var sized)&&sized.Width==8&&sized.Depth==6,"requested dimensions round to native wall sizes in rotated frame "+angle);
    sized.Point(0,out double sx,out double sz);g.Coordinates(sx,sz,out double sc,out double sr);
    Check(Math.Abs(sc-Math.Round(sc))<0.00001&&Math.Abs(sr-Math.Round(sr))<0.00001,"suggested room corners lie on native grid "+angle);
    Check(RectangleWallPlan.TryCreate(g,x2,z2,x1,z1,out var plan),"rectangle accepts reversed corners in rotated frame "+angle);
    var distant=new PlacementGrid(12345,-6789,angle,1);distant.World(2,3,out double fx1,out double fz1);distant.World(6,8,out double fx2,out double fz2);
    Check(RectangleWallPlan.TryCreate(distant,(float)fx1,(float)fz1,(float)fx2,(float)fz2,out _),"native float coordinates retain full-grid rectangle corners "+angle);
    Check(Math.Abs(plan.Width-4)<0.0001&&Math.Abs(plan.Depth-5)<0.0001,"physical rectangle dimensions "+angle);
    g.World(0,4,out double cx1,out double cz1);g.World(8,4,out double cx2,out double cz2);
    Check(plan.CrossesInterior(cx1,cz1,cx2,cz2),"rotated crossing detected even with both ends outside "+angle);
    plan.Point(0,out double ax,out double az);plan.Point(4,out double bx,out double bz);Check(Math.Abs(ax-bx)<0.0001&&Math.Abs(az-bz)<0.0001,"closed boundary "+angle);
    plan.Point(1,out double ex,out double ez);Check(!plan.CrossesInterior(ax,az,ex,ez),"shared boundary excluded from interior conflict "+angle);
    plan.Point(1,out bx,out bz);Check(plan.IsBoundaryEdge(ax,az,bx,bz),"native matching boundary "+angle);
    plan.Point(2,out bx,out bz);Check(!plan.IsBoundaryEdge(ax,az,bx,bz),"diagonal excluded "+angle);
   }
   var grid=new PlacementGrid(0,0,0,1);
   Check(!RectangleWallPlan.TryCreateSized(grid,0,0,0,6,out _)&&!RectangleWallPlan.TryCreateSized(grid,0,0,8,double.NaN,out _),"room dimensions reject zero and nonfinite sizes");
   Check(!RectangleWallPlan.TryCreate(grid,0,0,0,2,out _),"zero width rejected");
   Check(!RectangleWallPlan.TryCreate(grid,0.5,0,2,2,out _),"partial-cell corners do not silently resize plan");
   Check(!RectangleWallPlan.TryCreate(grid,double.NaN,0,2,2,out _),"nonfinite corner rejected");
   bool threw=false;try{grid.Step(0,0,1,0,double.NaN,out _,out _);}catch(ArgumentOutOfRangeException){threw=true;}Check(threw,"invalid native fine multiplier rejected");
   Reset();Settle();Check(WallConstructionHandler.OwnsInput&&LotBuildInputOwnership.OwnsInput,"wall tool owns shared navigation");
   Check(H.Player.FreezeCameraMovement,"arrows cannot pan camera");
   Check(WallConstructionHandler.TryGetContext(H,out string context)&&!context.Contains("wall_grid_context")&&!context.Contains("wall_position"),"ordinary wall F3 omits grid arithmetic");
   InputManager.Instance.Cursor=new Vector2(700,400);WallConstructionHandler.BeforeGameSystems();Check((bool)Get("_hasTarget"),"delayed legacy stream retains the construction guide");Mouse.current.position.Value=InputManager.Instance.Cursor;WallConstructionHandler.BeforeGameSystems();Check(!(bool)Get("_hasTarget"),"manual cursor movement releases requested point without warping back");
   WidgetManager.Instance.Cursor.Transform.position=new Vector3(3,0,4);End(false);Tick();Check(Math.Abs(Target.x-3)<0.001&&Math.Abs(Target.z-4)<0.001,"next pass adopts actual native mouse position");
   Reset();Settle();
   Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Keyboard.current.leftShiftKey.isPressed=true;Tick();Check(Math.Abs(Target.x-0.5)<0.0001,"construction Shift uses native half step");
   Enter();Check(H.ButtonConfirm.Count==0,"move followed by early Enter cannot start a stale wall");End();Settle();Enter();Check(H.ButtonConfirm.Count==1,"settled Enter starts native preview");
   Check((bool)Call("BeforeNativeStart",H.Player),"start guard accepts matching native cursor");
   WidgetManager.Instance.Cursor.Transform.position=new Vector3(10,0,10);Check(!(bool)Call("BeforeNativeStart",H.Player),"start guard blocks native cursor changes within same system pass");End(false);
   Check(Said("wall_start_refused"),"native start refusal announced");
   Reset();Settle();Enter();StartObserved(20);Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Tick();Settle();Enter();
   Check((bool)Call("BeforeNativeEnd",H.Player),"end guard accepts observed endpoint");
   WidgetManager.Instance.Cursor.Transform.position=new Vector3(9,0,9);Check(!(bool)Call("BeforeNativeEnd",H.Player),"end guard rejects a cursor newer than the native endpoint");End(false);
   Check(Said("wall_refused"),"unacknowledged placement does not report success");
   Settle();Enter();CommitObserved();Check(Said("wall_placed"),"native completion survives destruction or splitting of original segment");
   Check(!H.ButtonConfirm.Down&&!H.ButtonConfirm.Up&&!H.ButtonConfirm.Pressed,"synthetic confirmation cannot remain held or click an unrelated widget");
   Reset();WidgetManager.Instance.Cursor.Transform.position=new Vector3(100,0,100);Settle();Check(Math.Abs(Target.x)<0.001&&Math.Abs(Target.z)<0.001,"off-lot mouse begins at verified lot anchor");
   Reset();Settle();Enter();StartObserved(22);Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Tick();Settle();H.Player.SegmentInPlacement.Price=80;
   int priorPrice=H.ButtonConfirm.Count;Enter();Check(H.ButtonConfirm.Count==priorPrice,"unreviewed price change cannot be confirmed");End();Settle();
   Check(WallConstructionHandler.TryGetContext(H,out context)&&context.Contains("80"),"F3 reads current native price");
   Reset();Settle();Enter();StartObserved(21);Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Tick();Settle();
   SegmentManager.Instance.Walls.Add(new SegmentObject{NodeA=new NodeObject{Position=H.Player.SegmentInPlacement.NodeA.Position},NodeB=new NodeObject{Position=H.Player.SegmentInPlacement.NodeB.Position}});
   int beforeReplacement=H.ButtonConfirm.Count;Enter();Check(H.ButtonConfirm.Count==beforeReplacement&&!(bool)Call("BeforeNativeEnd",H.Player),"existing-wall replacement blocked before native refund or purchase");End();Check(Said("wall_replacement_unsupported"),"replacement refusal explains scope");
   Reset();Settle();InputManager.Instance.OverUI=true;Enter();Check(H.ButtonConfirm.Count==0&&WorldTargeting.Calls==1&&Said("wall_view_reframed"),"pointer over UI reframes without confirming");End(false);Enter();Check(H.ButtonConfirm.Count==0&&WorldTargeting.Calls==1&&Said("wall_pointer_over_ui"),"persistent UI obstruction has bounded recovery and actionable feedback");End(false);
   InputManager.Instance.OverUI=false;Settle();H.Player.NodeCursor.PositionIllegal=true;Enter();Check(H.ButtonConfirm.Count==0,"native illegal position cannot confirm");End();
   Reset();Settle();InputManager.ControlOrCommand=true;Enter();Check(H.ButtonConfirm.Count==0&&Said("wall_release_modifiers"),"Control does not convert straight confirmation into curve input");End();
   Reset();Settle();ItemPlacementHandler.Visible=false;Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Tick(false);Enter();Check(H.ButtonConfirm.Count==0,"out-of-view request cannot place stale geometry");End(false);
   Keyboard.current.homeKey.wasPressedThisFrame=true;Tick(false);Check(ItemPlacementHandler.Visible,"Home reframes an out-of-view target");
   Reset();Settle();H.HasInputFieldFocused=true;Tick(false);Check(!WallConstructionHandler.OwnsInput&&!H.Player.FreezeCameraMovement,"text field releases ownership and restores camera");
   Reset();Settle();H.Player.PlayerActionBlockedByUIWindow=true;Tick(false);Check(!WallConstructionHandler.OwnsInput,"native modal owns input");
   Reset();Settle();H.IsUsingGamePad=true;Tick(false);Check(!WallConstructionHandler.OwnsInput,"gamepad takes priority");
   Reset();Settle();H.Player.CurrentWallHeightType=WallHeightType.HalfWall;Tick(false);Check(!WallConstructionHandler.OwnsInput,"unsupported wall type is not captured");
   Reset();Settle();H.Player.WallPlacementMode=WallPlacementMode.Curved;Tick(false);Check(!WallConstructionHandler.OwnsInput,"curved tool is not captured");
   Reset();Settle();Keyboard.current.Pressed=Key.F6;WallConstructionHandler.BeforeGameSystems();Check(!LotBuildInputOwnership.OwnsInput,"idle wall cursor yields cleanly to F6 catalogue");End(false);
   Reset();Settle();Keyboard.current.Pressed=Key.F7;WallConstructionHandler.BeforeGameSystems();Check(H.Player.State==GameStates.BuildMode&&!LotBuildInputOwnership.OwnsInput,"F7 leaves idle wall tool via native banner close and releases input this frame");End(false);
   Reset();Settle();Enter();StartObserved(211);Settle();Keyboard.current.Pressed=Key.F7;WallConstructionHandler.BeforeGameSystems();Check(H.Player.State==GameStates.PlaceSegment&&H.Player.SegmentInPlacement!=null,"F7 cannot abandon an active wall preview");End(false);
   Reset();Settle();Settings.Get<Gameplay>().UsesWallGrid=false;Enter();Check(H.ButtonConfirm.Count==0&&Said("wall_grid_disabled"),"disabled native grid is not silently enabled");End();
   Reset();Settle();H.Player.Layer=1;Enter();Check(H.ButtonConfirm.Count==0&&Said("wall_grid_unavailable"),"floor change invalidates old grid cursor");End();
   Reset();Settle();ModInputKey.Available=false;Keyboard.current.Pressed=Key.R;Tick();Check(Stage=="None"&&Said("hotkey_conflict"),"live key conflict prevents rectangle command");
   Reset();RequestRectangle();Check(H.ButtonConfirm.Count==0,"planning corners does not inject native construction");
   for(int edge=0;edge<4;edge++){
    // No game implementation is simulated: supply the native start and endpoint observations.
    int before=H.ButtonConfirm.Count;for(int i=0;i<5&&!(bool)Get("_pendingStart");i++){WallConstructionHandler.BeforeGameSystems();if(!(bool)Get("_pendingStart"))End();}
    Check((bool)Get("_pendingStart"),"guide requests native start for edge "+edge);Call("BeforeNativeStart",H.Player);Check(H.ButtonConfirm.Count==before+1,"one start request per edge "+edge);
    StartObserved(100+edge);Settle();Check(Stage=="ReviewingEdge","guide prepares an endpoint for review "+edge);
    int count=H.ButtonConfirm.Count;Settle();Check(H.ButtonConfirm.Count==count,"edge requires explicit purchase confirmation "+edge);
    Enter();Check(H.ButtonConfirm.Count==count+1,"Enter confirms current edge only "+edge);Check((bool)Call("BeforeNativeEnd",H.Player),"native guard accepts planned boundary "+edge);
    CommitObserved();Check(Plan.CompletedEdges==edge+1,"only native acceptance advances plan "+edge);
   }
   Check(Stage=="CheckingRoom","four commits begin native room verification");
   var room=new ZoneWallObject{InstanceID=77,SurfaceArea=4};
   for(int i=0;i<4;i++){Plan.Point(i,out double ax,out double az);Plan.Point(i+1,out double bx,out double bz);room.Segments.Add(new ZoneSegment{Segment=new SegmentObject{NodeA=new NodeObject{Position=new Vector3((float)ax,0,(float)az)},NodeB=new NodeObject{Position=new Vector3((float)bx,0,(float)bz)}}});}
   ZoneManager.Instance.Zones.Add(room);Tick();Tick();Check(Said("wall_room_verified")&&Stage=="None","only matching new native zone is announced as a room");
   int completionSpeech=ScreenReader.Spoken.Count;for(int n=0;n<60;n++)Tick();
   Check(ScreenReader.Spoken.Count==completionSpeech,"idle wall-start prompt cannot interrupt successful room completion");
   Check(WallConstructionHandler.TryGetContext(H,out var completedContext)&&completedContext.Contains("wall_idle"),"F3 remains available while automatic idle speech is quiet");
   Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Tick();Settle();Check(ScreenReader.Spoken.Count==completionSpeech+1,"a fresh arrow resumes wall-cursor feedback after completion");
   Reset();RequestRectangle();CompleteEdges(4);Tick();Tick();Check(Said("wall_room_unverified")&&!Said("wall_room_verified"),"four purchases alone never invent an enclosed room");
   completionSpeech=ScreenReader.Spoken.Count;for(int n=0;n<60;n++)Tick();Check(ScreenReader.Spoken.Count==completionSpeech,"unverified room result also survives automatic idle settling");
  Reset();RequestRectangle();CompleteEdges(1);H.ButtonCancel.Down=true;Tick(false);Check(Stage=="None"&&Said("wall_rectangle_stopped(1)"),"partial cancellation reports actual completed count without rollback");
   Reset();RequestRectangle();CompleteEdges(1);
   for(int n=0;n<5&&!(bool)Get("_pendingStart");n++){WallConstructionHandler.BeforeGameSystems();if(!(bool)Get("_pendingStart"))End();}
   Check((bool)Get("_pendingStart"),"first wall commit prepares a separately reviewed second wall");
   Call("BeforeNativeStart",H.Player);StartObserved(485);Settle();ScreenReader.Spoken.Clear();int priorSecond=H.ButtonConfirm.Count;
   for(int frame=0;frame<400;frame++){
    WindowHandler.FloatingTextPostfix(1,"4 m");WindowHandler.FloatingTextPostfix(2,"48");WindowHandler.FloatingTextPostfix(5,"90 degrees");Tick();
   }
   Check(ScreenReader.Spoken.Count==0&&H.ButtonConfirm.Count==priorSecond&&Plan.CompletedEdges==1,"second-wall native length, price and angle redraws stay quiet without another purchase");
   Keyboard.current.leftShiftKey.isPressed=true;Tick();Keyboard.current.leftShiftKey.isPressed=true;Keyboard.current.Pressed=Key.J;Tick();
   Check(ScreenReader.Spoken.SequenceEqual(new[]{"whole lot description"})&&H.ButtonConfirm.Count==priorSecond&&Plan.CompletedEdges==1,
    "Shift pressed before J during the second wall produces exactly the requested report, no warning or purchase");
   for(int frame=0;frame<120;frame++){Keyboard.current.leftShiftKey.isPressed=true;Tick();}
   Check(ScreenReader.Spoken.Count==1,"holding Shift after the description never floods wall or description speech");Settle();
   WidgetManager.Instance.Angles.Add(new NodeAngleWidget{SignedAngle=-90});WidgetManager.Instance.Angles.Add(new NodeAngleWidget{SignedAngle=90});WidgetManager.Instance.Angles.Add(new NodeAngleWidget{SignedAngle=0});
   Keyboard.current.Pressed=Key.F11;Tick();Check(ScreenReader.Spoken.Last().Contains("wall_angle_markers(90 degrees)"),"F11 retains native angle markers once, with the same absolute-value display as the game");
   WidgetManager.Instance.Angles.Clear();WidgetManager.Instance.Angles.Add(new NodeAngleWidget{SignedAngle=135});Keyboard.current.Pressed=Key.F11;Tick();Check(ScreenReader.Spoken.Last().Contains("135 degrees")&&!ScreenReader.Spoken.Last().Contains("90 degrees"),"F11 reads changed live angles rather than a cached right angle");
   WidgetManager.Instance.Angles.Clear();Keyboard.current.Pressed=Key.F11;Tick();Check(!ScreenReader.Spoken.Last().Contains("wall_angle_markers"),"removed markers do not leave a stale angle report");
   WindowHandler.FloatingTextPostfix(4,"Native refusal");WindowHandler.FloatingTextPostfix(6,"Money spent");
   Check(Said("Native refusal")&&Said("Money spent"),"real wall controller ownership preserves native errors and transaction results");
   foreach(int modifier in new[]{0,1,2}){
    ScreenReader.Spoken.Clear();
    for(int frame=0;frame<120;frame++){InputManager.Alt=modifier==0;InputManager.ControlOrCommand=modifier==1;Keyboard.current.leftShiftKey.isPressed=modifier==2;Tick();}
    Check(ScreenReader.Spoken.Count(s=>s.StartsWith("wall_rectangle_locked"))==0,"a modifier alone stays quiet for read-only shortcuts: "+modifier);
    Keyboard.current.leftShiftKey.isPressed=modifier==2;Enter();Check(H.ButtonConfirm.Count==priorSecond&&Plan.CompletedEdges==1&&ScreenReader.Spoken.Count(s=>s.StartsWith("wall_rectangle_locked"))==1,"modified Enter warns once and still blocks wall confirmation: "+modifier);End();
    for(int frame=0;frame<120;frame++){Keyboard.current.leftShiftKey.isPressed=modifier==2;Tick();}
    Check(ScreenReader.Spoken.Count(s=>s.StartsWith("wall_rectangle_locked"))==1,"continuing to hold the modifier never floods the warning: "+modifier);
    InputManager.Alt=InputManager.ControlOrCommand=false;Settle();
   }
   ScreenReader.Spoken.Clear();InputManager.Alt=true;Enter();Check(Said("wall_rectangle_locked"),"releasing and attempting another modified edit restores warning");InputManager.Alt=false;Settle();Enter();CommitObserved();Check(Plan.CompletedEdges==2,"release and a fresh Enter allow the second wall purchase");
   Reset();RequestRectangle();CompleteEdges(4);BuildModeRefreshManager.IsZoneStateRefreshed=false;Tick();Tick();Check(Stage=="CheckingRoom"&&!Said("wall_room_unverified"),"room check waits for native zone refresh");Time.unscaledTime+=6;Tick();Check(Stage=="None"&&Said("wall_room_unverified"),"room verification times out on unscaled time without false success");
   Reset();RequestRectangle();CompleteEdges(4);room.Layer=1;ZoneManager.Instance.Zones.Add(room);Tick();Tick();Check(!Said("wall_room_verified"),"matching rectangle on another floor cannot verify this room");
   Reset();room.Layer=0;ZoneManager.Instance.Zones.Add(room);RequestRectangle();CompleteEdges(4);Tick();Tick();Check(!Said("wall_room_verified"),"pre-existing room ID cannot be reported as newly created");
   Reset();RequestRectangle();LotManager.Instance.Lot.Grid.Rotation=90;Tick();Check(Stage=="None"&&Said("placement_grid_changed"),"grid redefinition stops rectangle");
   Reset();RequestRectangle();H.ButtonUndo.Down=true;Tick(false);Check(Stage=="None"&&H.ButtonConfirm.Count==0,"undo stops draft without constructing walls");
   Reset();RequestRectangle();H.Player.StartNewSegmentNextFrame=true;int prior=H.ButtonConfirm.Count;Settle();Check(H.ButtonConfirm.Count==prior,"native chain pending never receives duplicate start");
   H.Player.StartNewSegmentNextFrame=false;H.Player.SegmentInPlacement=new SegmentObject{InstanceID=200,NodeA=new NodeObject{Position=Target},NodeB=new NodeObject{Position=Target}};Tick();Check(Stage=="ReviewingEdge","matching native chain is adopted");
   Settle();WidgetManager.Instance.Cursor.Transform.position=new Vector3(Target.x+0.5f,0,Target.z);H.Player.SegmentInPlacement.NodeB.Position=WidgetManager.Instance.Cursor.Transform.position;
   End(false);Tick(false);Tick(false);Enter();Check(H.ButtonConfirm.Count==prior&&Said("wall_rectangle_endpoint"),"native snap away from endpoint cannot change rectangle purchase");End(false);
   H.ButtonCancel.Down=true;WallConstructionHandler.BeforeGameSystems();Check(H.ButtonCancel.Down&&Stage=="None","native cancellation input remains intact");End(false);
   Reset();ItemPlacementHandler.Lot=0;Tick(false);Tick(false);Check(ScreenReader.Spoken.Count(s=>s.StartsWith("wall_lot_required"))==1,"missing owned lot gives one audible explanation without repeated speech");
   Reset();Settle();CoordinateEntryHandler.Requested=true;WallConstructionHandler.BeforeGameSystems();CoordinateEntryHandler.Requested=false;Check(CoordinateEntryHandler.Valid()&&H.ButtonConfirm.Count==0,"wall F10 opens a valid coordinate request without native confirmation");CoordinateEntryHandler.Apply(13,14);Check(Target.x==3&&Target.z==4,"wall columns and rows use existing native construction grid");Settle();WidgetManager.Instance.Cursor.Transform.position=new Vector3(3.1f,0,4);Tick(false);Tick(false);Tick(false);Enter();Check(H.ButtonConfirm.Count==0&&Said("wall_coordinate_mismatch"),"settled native snapping divergence cannot confirm numeric point");
   Reset();Settle();CoordinateEntryHandler.Requested=true;WallConstructionHandler.BeforeGameSystems();CoordinateEntryHandler.Requested=false;CoordinateEntryHandler.Apply(13,14);Settle();Enter();Check(H.ButtonConfirm.Count==1,"settled exact numeric wall point starts native preview");StartObserved(900);CoordinateEntryHandler.BlocksPlacement=true;H.Player.PlayerActionBlockedByUIWindow=true;WallConstructionHandler.BeforeGameSystems();WallConstructionHandler.AfterGameSystems();Check(Get("_player")==H.Player&&H.Player.SegmentInPlacement!=null,"owned coordinate form preserves native preview and wall session");CoordinateEntryHandler.BlocksPlacement=false;H.Player.PlayerActionBlockedByUIWindow=false;
   Reset();var sharedWall=new SegmentWallObject{InstanceID=77,NodeA=new NodeObject{Position=new Vector3(0,0,0)},NodeB=new NodeObject{Position=new Vector3(2,0,0)}};SegmentManager.Instance.Walls.Add(sharedWall);RequestRectangle(true);Check(H.ButtonConfirm.Count==0&&Plan.CompletedEdges==0,"adjoining-room review does not buy existing side");Settle();Enter();End();Check(Plan.CompletedEdges==1&&Stage=="StartingEdge"&&H.ButtonConfirm.Count==0,"separate Enter acknowledges shared boundary without synthetic confirmation");CompleteEdges(3);Check(Stage=="CheckingRoom"&&Plan.Complete&&sharedWall.gameObject.activeInHierarchy,"three separately confirmed new walls complete adjoining guide while retaining shared side");
   Reset();sharedWall=new SegmentWallObject{InstanceID=77,NodeA=new NodeObject{Position=new Vector3(0,0,0)},NodeB=new NodeObject{Position=new Vector3(2,0,0)}};SegmentManager.Instance.Walls.Add(sharedWall);RequestRectangle(true);sharedWall.InstanceID++;Settle();Enter();Check(H.ButtonConfirm.Count==0&&Plan.CompletedEdges==0,"changed shared-side identity blocks reuse before any new wall starts");
   Reset();Settle();Keyboard.current.Pressed=Key.R;Tick();Check(CoordinateEntryHandler.Valid()&&CoordinateEntryHandler.First==4&&CoordinateEntryHandler.Second==4&&Stage=="None"&&H.ButtonConfirm.Count==0,"R offers suggested room size without starting or buying a wall");
   CoordinateEntryHandler.Apply(8,6);Check(Plan.Width==8&&Plan.Depth==6&&Stage=="StartingEdge"&&H.ButtonConfirm.Count==0,"room dimensions prepare separately reviewed native walls");
   Reset();Settle();Keyboard.current.Pressed=Key.R;Tick();CoordinateEntryHandler.Apply(8,8);Check(Plan.Width==8&&Plan.Depth==8&&Stage=="StartingEdge"&&H.ButtonConfirm.Count==0,"reported 8;8 dimensions prepare an eight-metre square with no purchase");
   CompleteEdges(4);Check(Stage=="CheckingRoom","dimension-based room completes through all four native wall commits");
   Reset();Settle();Keyboard.current.Pressed=Key.R;Tick();CoordinateEntryHandler.Apply(-2,6);Check(Plan==null&&Stage=="None"&&Said("room_size_invalid"),"negative room width cannot start construction");
   int invalidSpeech=ScreenReader.Spoken.Count;for(int n=0;n<60;n++)Tick();
   Check(ScreenReader.Spoken.Count==invalidSpeech,"idle anchor settling cannot interrupt rejected room-size feedback");
   Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Tick();Settle();Check(ScreenReader.Spoken.Count==invalidSpeech+1,"new wall-cursor movement resumes feedback after a rejected size");
   Reset();Settle();Keyboard.current.Pressed=Key.R;Tick();ScreenReader.Spoken.Clear();Settle();
   Check(ScreenReader.Spoken.Count==0&&H.ButtonConfirm.Count==0,"cancelled room form does not announce an automatic anchor move or start a wall");
   Reset();Settle();Keyboard.current.Pressed=Key.R;Tick();CoordinateEntryHandler.Apply(100,100);Check(Plan==null&&Stage=="None"&&H.ButtonConfirm.Count==0&&Said("room_size_outside"),"oversize room refuses before native preview or purchase");
   Reset();MeasurementUnitService.Unit=new MeasurementUnit{MetricMultiplier=3.28084f,ShortName="ft"};Settle();Keyboard.current.Pressed=Key.R;Tick();Check(CoordinateEntryHandler.Hint.Contains("ft")&&Math.Abs(CoordinateEntryHandler.First-13.12336)<0.001,"room form uses current measurement unit for label and defaults");CoordinateEntryHandler.Apply(8*MeasurementUnitService.Unit.MetricMultiplier,6*MeasurementUnitService.Unit.MetricMultiplier);Check(Plan.Width==8&&Plan.Depth==6,"room input converts the chosen unit once before grid planning");
   Reset();Settle();Keyboard.current.Pressed=Key.R;Tick();MeasurementUnitService.Unit.MetricMultiplier=1;Check(!CoordinateEntryHandler.Valid(),"changing measurement units invalidates the open size form");MeasurementUnitService.Unit=new MeasurementUnit();
   Reset();Settle();Keyboard.current.Pressed=Key.R;Tick();LotManager.Instance.Lot.Grid.Rotation=30;Check(!CoordinateEntryHandler.Valid(),"changed lot frame invalidates room size form");
   Reset();Settle();Keyboard.current.Pressed=Key.F11;Tick();Check(Said("wall_position"),"F11 retains explicit exact wall position");
   Check(ScreenReader.Spoken.Any(s=>s.Contains("build_lot_bounds")&&s.Contains("Camera focus")),"wall F11 includes bounds and distinguishes camera location from the wall cursor");
   Reset();H.Player.State=GameStates.BuildMode;var extensionGrid=new PlacementGrid(-10,-10,0,1);RectangleWallPlan.TryCreate(extensionGrid,0,0,2,-2,out var extensionPlan);
   var extensionWall=new SegmentWallObject{InstanceID=700,NodeA=new NodeObject{Position=new Vector3(0,0,0)},NodeB=new NodeObject{Position=new Vector3(2,0,0)}};SegmentManager.Instance.Walls.Add(extensionWall);Check(SharedWallBoundary.Find(extensionPlan,1,0,6,out var extensionShared)&&extensionShared!=null,"extension test shared wall comes from production boundary reader");
   var extension=new RoomExtensionReview{Plan=extensionPlan,Shared=extensionShared};Check(WallConstructionHandler.StartExtension(H,extension)=="extension review"&&Stage=="ReviewingSharedEdge"&&H.ButtonConfirm.Count==0,"extension handoff reviews the shared side without purchasing");
   Settle();Check(WallConstructionHandler.TryGetContext(H,out var extensionContext)&&extensionContext=="extension review","F3 names extension relation and dimensions at shared-side review");
   extension.Failure="blocked room";Enter();End();Check(Stage=="ReviewingSharedEdge"&&H.ButtonConfirm.Count==0&&ScreenReader.Spoken.Contains("blocked room"),"changed available space blocks shared-wall acknowledgement");
   extension.Failure=null;Settle();Enter();End();Check(Plan.CompletedEdges==1&&Stage=="StartingEdge"&&H.ButtonConfirm.Count==0,"separate Enter reuses existing wall without purchasing");
   CompleteEdges(3);Check(Stage=="CheckingRoom"&&Plan.Complete,"extension follows three separately confirmed native new walls");
   Check(SegmentManager.Instance.Walls.Contains(extensionWall)&&extensionWall.gameObject.activeInHierarchy,"extension retains original shared wall");
   var extensionRoom=new ZoneWallObject{InstanceID=900,SurfaceArea=(float)(Plan.Width*Plan.Depth)};
   for(int i=0;i<4;i++){Plan.Point(i,out double ax,out double az);Plan.Point(i+1,out double bx,out double bz);extensionRoom.Segments.Add(new ZoneSegment{Segment=new SegmentObject{NodeA=new NodeObject{Position=new Vector3((float)ax,0,(float)az)},NodeB=new NodeObject{Position=new Vector3((float)bx,0,(float)bz)}}});}
   ZoneManager.Instance.Zones.Add(extensionRoom);Tick();Tick();Check(Said("wall_room_verified")&&Stage=="None","three new walls and one reused side verify the native extension room");
   completionSpeech=ScreenReader.Spoken.Count;for(int n=0;n<60;n++)Tick();Check(ScreenReader.Spoken.Count==completionSpeech,"extension completion remains the last automatic wall announcement");
   Reset();H.Player.State=GameStates.BuildMode;extension.Target.IsLive=false;Check(WallConstructionHandler.StartExtension(H,extension).Contains("extension_changed")&&H.ButtonConfirm.Count==0,"stale room target cannot start native extension handoff");
   return checks+" wall construction checks passed; Unity raycasts, speech, purchases, saves and native undo remain runtime checks.";
  }
 }
}
'@
$adapters += "`nnamespace ParalivesAccess.Handlers { public static class WindowHandler { public static bool AnnounceWindows=true; " + ($floatingMethods -join "`n") + " } }"
Add-Type -TypeDefinition ($adapters + "`n" + ($sources -join "`n")) -Language CSharp
[ParalivesAccess.Handlers.WallChecks]::Run()
