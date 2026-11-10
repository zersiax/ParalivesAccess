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
$handler = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/ItemPlacementHandler.cs')
$methods = @(
    'public static void BeforeGameSystems()', 'private static bool HasPlacementForeground(',
    'private static void MoveCursor(', 'private static bool RefreshGrid()',
    'internal static bool TryProjectTarget(', 'private static void RefuseTarget(',
    'private static void ObserveManualCursor(', 'internal static void MaintainWorldCursor(',
    'private static void WarpCursor(', 'private static bool IsFinite(',
    'internal static bool TryGetContext(', 'internal static bool TryGetDetailedContext(', 'private static bool GetContext(', 'private static string DescribeStatus(',
    'internal static void ResetRoomBoundaryTarget(', 'private static void ResumeFloorPlacement(', 'private static void OpenCoordinates(', 'internal static string RequestAlignment('
) | ForEach-Object { Read-Method $handler $_ }
$geometry = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/PlacementGrid.cs')
$reader = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/PlacementGridReader.cs')
# Compile production geometry/readers and actual movement/confirmation methods. Adapters replace
# Unity projection/input and native data only; they do not implement a second placement policy.
$adapters = @'
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using ParalivesAccess.Speech;
namespace UnityEngine {
 public struct Vector2 {public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}public static float Distance(Vector2 a,Vector2 b)=>(float)Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y));public static implicit operator Vector2(Vector3 v)=>new Vector2(v.x,v.y);}
 public struct Vector3 {public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}public override string ToString()=>$"{x},{y},{z}";}
 public struct Rect {public float xMin,yMin,width,height;public float xMax=>xMin+width;public float yMax=>yMin+height;}
 public class Camera {public Rect pixelRect=new Rect{xMin=100,yMin=50,width=800,height=600};public float Scale=20,OffsetX=500,OffsetY=350,Depth=1;public Vector3 WorldToScreenPoint(Vector3 v)=>new Vector3(OffsetX+v.x*Scale,OffsetY+v.z*Scale,Depth);}
 public class Transform {public Vector3 eulerAngles;}
}
namespace UnityEngine.InputSystem {
 public enum Key {Home}
 public class KeyState {public bool wasPressedThisFrame,wasReleasedThisFrame,isPressed;}
 public class Keyboard {public static Keyboard current=new Keyboard();public KeyState rightArrowKey=new KeyState(),leftArrowKey=new KeyState(),upArrowKey=new KeyState(),downArrowKey=new KeyState(),enterKey=new KeyState(),numpadEnterKey=new KeyState(),leftShiftKey=new KeyState(),rightShiftKey=new KeyState();}
 public class PointerControl {public Vector2 Value;public Vector2 ReadValue()=>Value;}
 public class Mouse {public static Mouse current=new Mouse();public PointerControl position=new PointerControl();public int Warps;public void WarpCursorPosition(Vector2 p){Warps++;position.Value=p;InputManager.Instance.Cursor=new Vector3(p.x,p.y,0);}}
}
public enum GameStates {BuildMode,LiveMode}
public class ItemCubeTransform {public Vector3 LocalPosition,FinalSize=new Vector3(1.2f,0.8f,0.6f);}
public class ItemObjectRoot {public int InstanceID=7;public bool Valid=true;public string Problem;public ItemCubeTransform CubeTransform=new ItemCubeTransform();public Transform transform=new Transform();}
public class ItemObjectInPlacement {public ItemObjectRoot Item=new ItemObjectRoot();public bool CursorTeleported,FromRotationWidget,IsInOffset,IsNewItem=true;public Vector3 CursorTeleportPosition;}
public class Player {public bool FreezeCameraMovement,PlayerActionBlockedByUIWindow,HasSnap;public int PlayerIndex,Layer;public object ItemInRotation;public int GetLotLayer(ulong lot)=>Layer;public GameStates State=GameStates.BuildMode;public ItemObjectInPlacement ItemInPlacement=new ItemObjectInPlacement();}
public class Button {public bool Down;public int Confirms;public void SetValue(float v){if(v==0)Confirms++;}}
public class HybridCamera {public Camera Camera=new Camera();}
public class HybridPlayer {public Player Player=new Player();public HybridCamera HybridCamera=new HybridCamera();public bool HasInputFieldFocused,IsUsingGamePad;public Button ButtonSmallRotateItemLeft=new Button(),ButtonSmallRotateItemRight=new Button(),ButtonLargeRotateItem=new Button(),ButtonConfirm=new Button();}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public class InputManager {public static InputManager Instance=new InputManager();public Vector3 Cursor;public bool OverUI;public Vector3 GetCursorPosition(int id)=>Cursor;public bool IsPointerOverUI(int id)=>OverUI;}
public class AssetLotGridData {public Vector2 OffsetFromOrigin;public float Rotation;}
public class AssetLot {public AssetLotGridData Grid=new AssetLotGridData();public AssetLotGridData GetGridData()=>Grid;}
public class LotPerimeterZoneObject {public List<int> Segments=new List<int>{1,2,3,4};public List<Vector3> Points=new List<Vector3>{new Vector3(-10,0,-10),new Vector3(10,0,-10),new Vector3(10,0,10),new Vector3(-10,0,10)};public List<Vector3> GetPoints()=>Points;}
public class LotManager {public static LotManager Instance=new LotManager();public AssetLot Lot=new AssetLot();public AssetLot GetLotByGUID(ulong id)=>Lot;}
public class ZoneManager {public static ZoneManager Instance=new ZoneManager();public LotPerimeterZoneObject Perimeter=new LotPerimeterZoneObject();public LotPerimeterZoneObject GetLotPerimeterZoneObjectByGUID(ulong id)=>Perimeter;}
public static class MeasurementUnitService {public static string FormatLength(float v,string format="0.0")=>v.ToString(format)+" m";}
public static class TranslationManager {public static string Get(string key)=>key;}
namespace ParalivesAccess.Speech {public static class ScreenReader {public static string NormalizeForOutput(string s)=>s;}}
namespace ParalivesAccess.Handlers { public static class CeilingPlacementHandler {public static void Before(HybridPlayer h,ItemObjectInPlacement p){}public static string Describe(bool detailed=false)=>"ceiling";public static string Problem(HybridPlayer h,ItemObjectRoot r)=>null;}
 public static class NativePlacementAttempt {public static bool Before(HybridPlayer h,ItemObjectInPlacement p)=>false;}
 public static class RoomFitSearch {public static bool Before(HybridPlayer h,ItemObjectInPlacement p)=>false;}
 public static class RoofClearanceReader {public static string Problem(ItemObjectRoot r)=>null;}
 public static class ModInputKey {public static bool WasPressed(Keyboard k,Key key)=>false;public static bool IsAvailable(HybridPlayer h,Key key,out string conflict){conflict=null;return true;}}
 public static class PlacementSupportConsent {public static string FloorFailure;public static string FloorProblem(ItemObjectInPlacement p)=>FloorFailure;public static void Clear(){}public static string Problem(ItemObjectInPlacement p)=>null;public static string Describe(ItemObjectInPlacement p)=>null;}
 public static class PlacementLocationReader {public static string Location;public static string Describe(ItemObjectRoot item)=>Location;}
 internal static class RoomPlacementGrid {public static bool Selected;public static string Failure;public static string Problem(ulong lot,int layer,Vector3 p)=>Failure;public static string Describe(ItemObjectRoot i)=>"grid frame";public static bool TryRead(ulong lot,int layer,float spacing,out PlacementGrid grid)=>PlacementGridReader.TryRead(lot,spacing,out grid);}
 public static class RoofConstructionHandler {public static bool CanOverlap(ItemObjectRoot a,ItemObjectRoot b)=>false;public static bool Pending(ItemObjectInPlacement p)=>false;public static string Problem(HybridPlayer h,ItemObjectInPlacement p)=>null;public static void ClearFit(ItemObjectInPlacement p){}public static void TickFit(HybridPlayer h,ItemObjectInPlacement p){}public static string Describe(ItemObjectRoot r)=>null;public static void Clear(){}}
 public static class PlacementActionsHandler {public static bool Active;public static bool Handle(HybridPlayer h,ItemObjectInPlacement p)=>Active;public static string Describe()=>"commands";}
 public static class FacingTargetHandler {public static string Failure;public static string Problem(HybridPlayer h,ItemObjectRoot i)=>Failure;public static string Describe(HybridPlayer h,ItemObjectRoot i)=>"facing";}
 public static class ItemAlignmentHandler {public static string Failure;public static string Problem(HybridPlayer h,ItemObjectRoot i)=>Failure;public static string Describe(HybridPlayer h,ItemObjectRoot i)=>"alignment";}
 public static class CoordinateEntryHandler {public static bool BlocksPlacement,Requested;public static Action<double,double> Apply;public static Func<bool> Valid;public static bool WasRequested(HybridPlayer h)=>Requested;public static void Open(HybridPlayer h,ItemObjectInPlacement p,string hint,double x,double z,Func<bool> valid,Action<double,double> apply){Valid=valid;Apply=apply;}}
 public static class ExistingItemEditHandler {public static int Queued;public static void QueueConfirm(){Queued++;}}
 public static class ItemFootprintReader {public static string Describe(ItemObjectRoot item)=>"footprint";}
 public static class WallItemPlacementHandler {public static int Calls;public static void BeforeGameSystems(HybridPlayer h,ItemObjectInPlacement p){Calls++;}public static string Describe(bool detailed=false)=>"wall";}
 public static class SlotPlacementHandler {public static void Clear(){Active=false;}public static bool Active,Opening,CloseOnBefore;public static int Calls;public static bool TryOpen(HybridPlayer h,ItemObjectInPlacement p){if(Opening)Active=true;return Opening;}public static void BeforeGameSystems(HybridPlayer h,ItemObjectInPlacement p){Calls++;if(CloseOnBefore)Active=false;}public static string Describe(bool detailed=false)=>"slots";public static Vector3 AimPoint(Player p,ItemObjectInPlacement i,Vector3 point)=>point;}
 public class Logger {public void LogInfo(string s){}}
 public static class Plugin {public static Logger Log=new Logger();}
 public static class Loc {public static string Get(string key,params object[] args)=>key+"("+string.Join(";",args)+")";}
 public static class ItemDescription {public static string GetName(ItemObjectRoot r)=>"Blocker";public static string GetProductIdentity(ItemObjectRoot r)=>GetName(r);public static string DescribeLocation(ItemObjectRoot r,Vector3? p)=>"Floor 0";}
 public static class PlacementChecks {
  private enum PlacementState {None,Centering,Moving,Wall,Ceiling,Unsupported}
  private static PlacementState _state=PlacementState.Moving;
  private static int _trackedInstanceId=7,_validatedFrames=1;
  private static ulong _expectedLotGuid=1;
  private static string _itemName="Desk",_targetFailure,LastSpeech,_expectedLotSource,_lastStatusSignature;
  private static Vector2 _cursorTarget;
  private static readonly PlacementPointer Pointer=new PlacementPointer();
  private static bool _restoringRoomBoundary;
  private static string _roomBoundaryMessage;
  private static PlacementGrid _grid;
  private static Vector3 _requestedPosition;
  private static bool _hasRequestedPosition,_movementAnnouncementPending;
  private static bool _requireExactTarget,_actionAim;private static int _coordinateLayer;private static PlacementGrid _coordinateGrid;
  public static bool Enabled=true;
  public static float GridSpacingMetres=0.5f;
  public static bool OwnsInput=>_trackedInstanceId>=0 && HasPlacementForeground(PlayerManager.Instance?.HybridPlayer1);
  private static ItemObjectRoot Blocker;
  private static string GetPlacementProblem(ItemObjectRoot r)=>r.Problem;
  private static bool IsInsideOwnedLot(ItemObjectRoot r)=>r.Valid;
  private static ItemObjectRoot GetBlockingObject(Player p,ItemObjectRoot r)=>Blocker;
  private static string DescribeDiagnostic(Player p,ItemObjectRoot r)=>"diagnostic";
  private static void TryWarpToInitialPoint(HybridPlayer p,ItemObjectInPlacement i){}
  private static void BeginTracking(HybridPlayer h,ItemObjectInPlacement p,bool centerHouseView,bool keepCameraBaseline){}
  private static void ReframeCurrentPoint(HybridPlayer h,ItemObjectInPlacement p){Speak("placement_pointer_over_ui");}
  private static ulong ResolvePlacementLot(Player p,out string source){source="camera";return 1;}
  private static void DisableAfterException(string phase,Exception e){throw new Exception(phase,e);}
  private static void Speak(string message){LastSpeech=message;}
  METHODS
  private static int checks;
  private static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
  private static bool Near(double a,double b)=>Math.Abs(a-b)<0.0001;
  private static HybridPlayer Reset(){
   RoomBoundaryConstraint.Enabled=false;RoomBoundaryConstraint.Evaluate=null;RoomPlacementGrid.Selected=PlacementActionsHandler.Active=false;RoomPlacementGrid.Failure=FacingTargetHandler.Failure=ItemAlignmentHandler.Failure=null;
   PlayerManager.Instance=new PlayerManager();InputManager.Instance=new InputManager();Mouse.current=new Mouse();Keyboard.current=new Keyboard();Pointer.Clear();
   LotManager.Instance=new LotManager();ZoneManager.Instance=new ZoneManager();
   SlotPlacementHandler.Active=SlotPlacementHandler.Opening=SlotPlacementHandler.CloseOnBefore=false;SlotPlacementHandler.Calls=0;
   CoordinateEntryHandler.BlocksPlacement=CoordinateEntryHandler.Requested=false;ExistingItemEditHandler.Queued=0;_requireExactTarget=false;
   _grid=null;_hasRequestedPosition=false;_targetFailure=null;_validatedFrames=1;_movementAnnouncementPending=false;_trackedInstanceId=7;_state=PlacementState.Moving;Blocker=null;Enabled=true;LastSpeech=null;
   PlacementLocationReader.Location=null;PlacementSupportConsent.FloorFailure=null;
   return PlayerManager.Instance.HybridPlayer1;
  }
  private static void Settle(HybridPlayer p){p.Player.ItemInPlacement.CursorTeleported=false;_validatedFrames=1;p.Player.ItemInPlacement.Item.CubeTransform.LocalPosition=_requestedPosition;}
  public static string Run(){
   foreach(double angle in new[]{0,30,90,-45,180,270}){
    var g=new PlacementGrid(213.25,-147.75,angle,0.5);g.World(7,-4,out double x,out double z);g.Coordinates(x,z,out double c,out double r);
    Check(Near(c,7)&&Near(r,-4),"rotated translated frame round trip "+angle);
    g.Step(x,z,1,0,false,out double nx,out double nz);Check(Near(Math.Sqrt((nx-x)*(nx-x)+(nz-z)*(nz-z)),0.5),"physical distance independent of angle "+angle);
    g.Step(nx,nz,-1,0,false,out nx,out nz);Check(Near(x,nx)&&Near(z,nz),"reverse movement "+angle);
   }
   var simple=new PlacementGrid(0,0,0,0.5);simple.Step(0.12,0.18,1,0,false,out double sx,out double sz);Check(Near(sx,0.5)&&Near(sz,0.18),"off-grid start reaches next line without changing other axis");
   foreach(double angle in new[]{30,90,-45,127}){
    var distant=new PlacementGrid(12345,-6789,angle,0.5);distant.World(2,3,out double dx,out double dz);bool progressed=true;
    for(int i=0;i<100;i++){double ox=(float)dx,oz=(float)dz;distant.Step(ox,oz,1,0,true,out dx,out dz);double distance=Math.Sqrt((dx-ox)*(dx-ox)+(dz-oz)*(dz-oz));if(Math.Abs(distance-0.1)>0.003)progressed=false;}
    Check(progressed,"native float coordinates do not stall or skip fine grid steps at angle "+angle);
   }
   simple.Step(-0.12,0,-1,0,false,out sx,out sz);Check(Near(sx,-0.5),"negative coordinate uses next line in requested direction");
   simple.Step(0.5,0,1,0,true,out sx,out sz);Check(Near(sx,0.6),"fine step is one fifth");
   for(int i=0;i<1000;i++)simple.Step(sx,sz,1,0,true,out sx,out sz);Check(Near(sx,100.6),"many fine steps retain lattice without drift");
   bool rejected=false;try{new PlacementGrid(0,0,0,double.NaN);}catch(ArgumentOutOfRangeException){rejected=true;}Check(rejected,"nonfinite spacing rejected");
   var p=Reset();Check(PlacementGridReader.TryRead(1,0.5f,out var frame)&&Near(frame.OriginX,-10)&&Near(frame.OriginZ,-10),"lot bounds provide reproducible local origin");
   ZoneManager.Instance.Perimeter.Points.Reverse();PlacementGridReader.TryRead(1,0.5f,out var reversed);Check(frame.SameFrame(reversed),"boundary enumeration order does not change addresses");
   LotManager.Instance.Lot.Grid.Rotation=90;PlacementGridReader.TryRead(1,0.5f,out var rotated);Check(!frame.SameFrame(rotated),"native grid redefinition changes overlay frame");
   Check(Near(rotated.OriginX,10)&&Near(rotated.OriginZ,-10),"right-angle roundoff does not add an origin cell");
   ZoneManager.Instance.Perimeter.Points[0]=new Vector3(float.NaN,0,0);Check(!PlacementGridReader.TryRead(1,0.5f,out frame),"invalid boundary rejected");
   p=Reset();Keyboard.current.rightArrowKey.wasPressedThisFrame=true;BeforeGameSystems();
   Check(_hasRequestedPosition&&Near(_requestedPosition.x,0.5),"real prefix requests one grid step");Check(Mouse.current.Warps==1&&p.Player.ItemInPlacement.CursorTeleported&&_validatedFrames==0,"warp retains native asynchronous handoff and resets validation");
   Check(_movementAnnouncementPending,"movement awaits actual-result announcement");
   Check(Near(_cursorTarget.x,510),"world target is projected through camera");
   Settle(p);p.Player.ItemInPlacement.Item.CubeTransform.LocalPosition=new Vector3(0.7f,0,0);BeforeGameSystems();Check(Near(_requestedPosition.x,1),"snapping does not shift subsequent requested grid target");
   Settle(p);Keyboard.current.rightShiftKey.isPressed=true;BeforeGameSystems();Check(Near(_requestedPosition.x,1.1),"prefix routes fine modifier");
   p=Reset();p.HybridCamera.Camera.Scale=70;MoveCursor(p,p.Player.ItemInPlacement,1,0,false);Check(Near(_requestedPosition.x,0.5)&&Near(_cursorTarget.x,535),"zoom changes pixels but not world request");
   Settle(p);p.HybridCamera.Camera.OffsetX+=100;MaintainWorldCursor(p,p.Player.ItemInPlacement);Check(Near(_requestedPosition.x,0.5)&&Near(_cursorTarget.x,635),"camera change reprojects same world target");
   Settle(p);ObserveManualCursor(p,p.Player.ItemInPlacement);InputManager.Instance.Cursor=new Vector3(_cursorTarget.x+10,_cursorTarget.y,0);ObserveManualCursor(p,p.Player.ItemInPlacement);Check(_hasRequestedPosition,"stale legacy position retains the keyboard floor target");Mouse.current.position.Value=InputManager.Instance.Cursor;ObserveManualCursor(p,p.Player.ItemInPlacement);Check(!_hasRequestedPosition&&_validatedFrames==0,"manual mouse movement releases requested target and invalidates confirm");
   p=Reset();p.HybridCamera.Camera.Depth=-1;MoveCursor(p,p.Player.ItemInPlacement,1,0,false);Check(Mouse.current.Warps==0&&_targetFailure!=null&&!_hasRequestedPosition,"behind-camera target refused without movement");
   p=Reset();p.HybridCamera.Camera.OffsetX=899;MoveCursor(p,p.Player.ItemInPlacement,1,0,false);Check(Mouse.current.Warps==0&&_targetFailure!=null,"out-of-view target not clamped to screen edge");
   Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0,"failed target cannot confirm stale location");
   p.HybridCamera.Camera.OffsetX=500;Keyboard.current.enterKey.wasReleasedThisFrame=false;Keyboard.current.rightArrowKey.wasPressedThisFrame=true;BeforeGameSystems();Check(_targetFailure==null&&Mouse.current.Warps==1,"next visible movement recovers from failure");
   p=Reset();Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0,"movement and confirm in same frame refused");
   Settle(p);Keyboard.current.rightArrowKey.wasPressedThisFrame=false;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==1,"settled movement allows native confirmation");
   p=Reset();Keyboard.current.enterKey.wasReleasedThisFrame=true;p.ButtonLargeRotateItem.Down=true;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0,"rotation and confirm require another validation pass");
   p=Reset();Keyboard.current.enterKey.wasReleasedThisFrame=true;InputManager.Instance.OverUI=true;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0,"pointer over interface refuses confirmation");
   p=Reset();Keyboard.current.enterKey.wasReleasedThisFrame=true;Blocker=new ItemObjectRoot();BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0,"occupied preview retains safety gate");
   p=Reset();p.HasInputFieldFocused=true;Keyboard.current.rightArrowKey.wasPressedThisFrame=true;BeforeGameSystems();Check(Mouse.current.Warps==0&&!OwnsInput,"text editing owns input");
   p.HasInputFieldFocused=false;p.Player.PlayerActionBlockedByUIWindow=true;BeforeGameSystems();Check(Mouse.current.Warps==0&&!OwnsInput,"blocking modal owns input");
   Check(!TryGetContext(p,out string report),"placement report does not hide modal context");
   p.Player.PlayerActionBlockedByUIWindow=false;p.IsUsingGamePad=true;Check(!OwnsInput,"gamepad screen-centre mode does not receive keyboard warps");
   p=Reset();MoveCursor(p,p.Player.ItemInPlacement,1,0,false);Settle(p);p.Player.ItemInPlacement.Item.CubeTransform.LocalPosition=new Vector3(0.7f,0,0);p.Player.HasSnap=true;
   Check(TryGetContext(p,out report)&&report.StartsWith("placement_context_short")&&!report.Contains("21.4")&&!report.Contains("placement_target_difference")&&!report.Contains("footprint"),"ordinary F3 omits coordinates, rounding noise and footprint lecture");
   Check(TryGetDetailedContext(p,out report)&&report.Contains("21.4")&&report.Contains("placement_target_difference(0.20 m)")&&report.Contains("placement_snapped"),"explicit detail reports actual fractional position and snap deviation");
   PlacementLocationReader.Location="In Living room, middle centre";
   Check(TryGetContext(p,out var placedReport)&&placedReport.Contains(PlacementLocationReader.Location),"ordinary placement description includes observed room region");
   _targetFailure="Cannot reach target";Check(DescribeStatus(p,p.Player.ItemInPlacement.Item).Contains(PlacementLocationReader.Location)&&DescribeStatus(p,p.Player.ItemInPlacement.Item).Contains(_targetFailure),"failed movement keeps actual location and refusal together");
   _targetFailure=null;PlacementLocationReader.Location=null;
   Check(report.Contains("1.20 m")&&report.Contains("0.60 m")&&report.Contains("0.80 m"),"F3 reads native dimensions");
   p.Player.ItemInPlacement.Item.CubeTransform.FinalSize=new Vector3(2,3,4);TryGetContext(p,out report);Check(report.Contains("2.00 m")&&report.Contains("4.00 m")&&report.Contains("3.00 m"),"dimensions read live after resize");
   p.Player.ItemInPlacement.Item.transform.eulerAngles=new Vector3(0,90,0);TryGetDetailedContext(p,out report);Check(report.Contains("placement_rotation(90)"),"explicit detail reads actual rotation");
   LotManager.Instance.Lot.Grid.Rotation=45;TryGetContext(p,out report);Check(!_hasRequestedPosition&&Near(_grid.Rotation,45),"changed native frame discards stale movement request");
   p=Reset();LotManager.Instance.Lot=null;Keyboard.current.rightArrowKey.wasPressedThisFrame=true;BeforeGameSystems();Check(_targetFailure!=null&&Mouse.current.Warps==0,"missing lot announces refusal");
   p=Reset();_state=PlacementState.Wall;Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();Check(WallItemPlacementHandler.Calls==1&&Mouse.current.Warps==0&&p.ButtonConfirm.Confirms==0,"wall previews delegate without floor-grid movement or floor confirmation");
   Check(TryGetContext(p,out report)&&report.StartsWith("wall")&&!report.Contains("footprint"),"wall F3 delegates without detailed footprint");
   Check(TryGetDetailedContext(p,out report)&&report.Contains("footprint"),"wall detail retains explicit footprint");
   p=Reset();SlotPlacementHandler.Opening=true;Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();Check(SlotPlacementHandler.Active&&Mouse.current.Warps==0&&p.ButtonConfirm.Confirms==0,"opening slots consumes floor movement and Enter");
   SlotPlacementHandler.Opening=false;BeforeGameSystems();Check(SlotPlacementHandler.Calls==1&&Mouse.current.Warps==0&&p.ButtonConfirm.Confirms==0,"active slots own arrows and confirmation instead of floor bridge");
   Check(TryGetContext(p,out report)&&report.StartsWith("slots"),"slot F3 delegates to semantic target context");
   _hasRequestedPosition=true;SlotPlacementHandler.CloseOnBefore=true;BeforeGameSystems();Check(!SlotPlacementHandler.Active&&!_hasRequestedPosition&&_validatedFrames==0&&_state==PlacementState.Moving&&_lastStatusSignature==null&&p.ButtonConfirm.Confirms==0,"leaving picker clears stale floor target and requires fresh validation");
   p=Reset();CoordinateEntryHandler.Requested=true;BeforeGameSystems();Check(CoordinateEntryHandler.Valid()&&Mouse.current.Warps==0,"coordinate form only reviews the current frame");CoordinateEntryHandler.Requested=false;CoordinateEntryHandler.Apply(5,5);Check(_requireExactTarget&&Near(_requestedPosition.x,-7.5)&&Near(_requestedPosition.z,-7.5),"numeric columns and rows resolve in the same lot frame");Settle(p);Keyboard.current.enterKey.wasReleasedThisFrame=true;p.Player.ItemInPlacement.Item.CubeTransform.LocalPosition=new Vector3(-7.4f,0,-7.5f);BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0&&LastSpeech.Contains("coordinate_mismatch"),"precise coordinate request refuses native snap displacement");
   p.Player.ItemInPlacement.Item.CubeTransform.LocalPosition=_requestedPosition;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==1,"exact observed coordinate can be confirmed later");
   p.ButtonConfirm.Confirms=0;p.Player.ItemInPlacement.Item.CubeTransform.LocalPosition=new Vector3(float.NaN,0,0);BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0,"nonfinite native observation cannot satisfy exact coordinate request");
   p=Reset();CoordinateEntryHandler.BlocksPlacement=true;Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0,"coordinate form Enter never reaches placement bridge");
   p=Reset();p.Player.ItemInPlacement.IsNewItem=false;Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();Check(ExistingItemEditHandler.Queued==1&&p.ButtonConfirm.Confirms==0,"existing floor move queues a scoped native confirmation");
   p=Reset();PlacementActionsHandler.Active=true;Keyboard.current.enterKey.wasReleasedThisFrame=true;Keyboard.current.rightArrowKey.wasPressedThisFrame=true;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0&&Mouse.current.Warps==0,"F11 command input cannot also move or confirm parent preview");TryGetContext(p,out string commandReport);Check(commandReport.StartsWith("commands"),"F3 reads placement command selection");
   p=Reset();RoomPlacementGrid.Failure="room_grid_outside";Keyboard.current.rightArrowKey.wasPressedThisFrame=true;BeforeGameSystems();Check(Mouse.current.Warps==0&&LastSpeech=="room_grid_outside","native room exterior refuses arrow request before cursor warp");Keyboard.current.rightArrowKey.wasPressedThisFrame=false;Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0,"refused room request cannot confirm previous valid position");
   p=Reset();CoordinateEntryHandler.Requested=true;BeforeGameSystems();RoomPlacementGrid.Failure="room_grid_outside";CoordinateEntryHandler.Apply(5,5);Check(Mouse.current.Warps==0&&LastSpeech=="room_grid_outside","numeric room request uses same native boundary guard");
   p=Reset();FacingTargetHandler.Failure="facing_mismatch";Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0&&LastSpeech=="facing_mismatch","unverified geometric facing blocks parent Enter");
   p=Reset();RoomPlacementGrid.Selected=true;TryGetDetailedContext(p,out string roomReport);Check(roomReport.StartsWith("placement_room_context"),"selected room detail cannot claim lot-origin coordinates");
   p=Reset();var desired=new Vector3(1.25f,0,2.5f);Check(RequestAlignment(p,p.Player.ItemInPlacement,desired)==null&&_requireExactTarget&&_actionAim&&_hasRequestedPosition&&Mouse.current.Warps==1&&p.ButtonConfirm.Confirms==0,"alignment routes through actual cursor request with exact-position validation and separate Enter");Check(_coordinateLayer==0&&_coordinateGrid!=null,"alignment snapshots active frame and layer");Settle(p);ItemAlignmentHandler.Failure="alignment_changed";Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0&&LastSpeech=="alignment_changed","stale alignment blocks parent floor confirmation");ItemAlignmentHandler.Failure=null;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==1,"valid observed alignment uses ordinary parent native confirmation");
   p=Reset();RoomPlacementGrid.Failure="room_grid_outside";Check(RequestAlignment(p,p.Player.ItemInPlacement,desired)=="room_grid_outside"&&Mouse.current.Warps==0,"alignment request respects native selected-room boundary before warp");
   p=Reset();Check(RequestAlignment(p,p.Player.ItemInPlacement,new Vector3(float.NaN,0,1)).Contains("alignment_unavailable")&&Mouse.current.Warps==0,"nonfinite alignment cannot poison cursor request");
   p=Reset();RoomBoundaryConstraint.Evaluate=(lot,layer,point)=>point.x>0.25f?"Living room boundary":null;
   Keyboard.current.rightArrowKey.wasPressedThisFrame=true;BeforeGameSystems();Check(Mouse.current.Warps==0&&!_hasRequestedPosition&&LastSpeech=="Living room boundary","room toggle stops an outward arrow before cursor warp");
   Keyboard.current.rightArrowKey.wasPressedThisFrame=false;Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();Check(p.ButtonConfirm.Confirms==1,"stopped arrow leaves the last valid preview placeable");p.ButtonConfirm.Confirms=0;Keyboard.current.enterKey.wasReleasedThisFrame=false;
   Keyboard.current.rightArrowKey.wasPressedThisFrame=false;Keyboard.current.leftArrowKey.wasPressedThisFrame=true;BeforeGameSystems();Check(Mouse.current.Warps==1&&_requestedPosition.x<0&&_targetFailure==null,"inward movement immediately recovers from boundary refusal");
   Settle(p);Keyboard.current.leftArrowKey.wasPressedThisFrame=false;Keyboard.current.enterKey.wasReleasedThisFrame=true;p.Player.ItemInPlacement.Item.CubeTransform.LocalPosition=new Vector3(1,0,0);BeforeGameSystems();Check(p.ButtonConfirm.Confirms==0&&LastSpeech=="Living room boundary","native snap beyond the room blocks keyboard confirmation");
   p=Reset();RoomBoundaryConstraint.Evaluate=(lot,layer,point)=>point.x>0.25f?"Room edge":null;CoordinateEntryHandler.Requested=true;BeforeGameSystems();CoordinateEntryHandler.Apply(22,20);
   Check(Mouse.current.Warps==0&&LastSpeech=="Room edge","F10 cannot send an outside-room target");
   Check(RequestAlignment(p,p.Player.ItemInPlacement,new Vector3(1,0,0))=="Room edge"&&Mouse.current.Warps==0,"object and wall alignment targets obey the same room boundary");
   RoomBoundaryConstraint.Evaluate=null;ResetRoomBoundaryTarget();Check(_targetFailure==null&&!_hasRequestedPosition&&!_requireExactTarget&&_validatedFrames==0,"toggle change discards refused or precise targets without movement");
   CoordinateEntryHandler.Requested=false;Keyboard.current.rightArrowKey.wasPressedThisFrame=true;BeforeGameSystems();Check(Mouse.current.Warps==1,"turning boundary off restores free keyboard movement");
   p=Reset();_restoringRoomBoundary=true;Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Keyboard.current.enterKey.wasReleasedThisFrame=true;
   BeforeGameSystems();Check(Mouse.current.Warps==0&&p.ButtonConfirm.Confirms==0,"pending native boundary return blocks further movement and confirmation");
   ResetRoomBoundaryTarget();Check(!_restoringRoomBoundary&&_roomBoundaryMessage==null,"toggle clears pending recovery without cancelling the preview");
   p=Reset();PlacementSupportConsent.FloorFailure="below actual floor";Keyboard.current.enterKey.wasReleasedThisFrame=true;BeforeGameSystems();
   Check(p.ButtonConfirm.Confirms==0&&LastSpeech=="below actual floor","floor-height refusal reaches keyboard confirmation before native purchase");
   Check(DescribeStatus(p,p.Player.ItemInPlacement.Item).Contains("below actual floor")&&!DescribeStatus(p,p.Player.ItemInPlacement.Item).Contains("placement_valid"),"below-floor feedback never claims placeable");
   return checks+" placement-grid checks passed; native raycasts, speech and saved placement require in-game verification.";
  }
 }
}
'@
# These fields are only consulted by context reporting for deferred placement kinds.
$adapters = $adapters.Replace('public bool Valid=true;', 'public bool Valid=true,IsWallItem,IsCeilingItem;')
$adapters = $adapters.Replace('METHODS', ($methods -join "`n"))
$geometry = [regex]::Replace($geometry, '(?m)^using .*;\r?\n', '')
$reader = [regex]::Replace($reader, '(?m)^using .*;\r?\n', '')
$boundaryAdapter = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomBoundaryAdapter.cs')
$pointer = (Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/PlacementPointer.cs')) -replace '(?m)^using [^;]+;\r?\n', ''
Add-Type -TypeDefinition ($adapters + "`n" + $geometry + "`n" + $reader + "`n" + $boundaryAdapter + "`n" + $pointer) -Language CSharp
[ParalivesAccess.Handlers.PlacementChecks]::Run()
