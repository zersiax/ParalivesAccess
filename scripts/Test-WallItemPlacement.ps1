$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
# Compile the full face reader and wall-item controller. Native results/rays are supplied as
# observations; these adapters do not simulate a purchase, hole mesh, undo stack or game save.
$sources = @('PlacementGrid','PlacementPointer','WallFaceReader','WallSpanReader','DoorPlacementReader','WallItemPlacementHandler') | ForEach-Object {
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
$guard = @('internal static string SemanticPlacementProblem(', 'private static ItemObjectRoot GetBlockingObject(', 'private static void ResetBlockingObjectCache(') | ForEach-Object { Read-Method $_ }
$adapters = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Setting;
using UnityEngine;
using UnityEngine.InputSystem;
using ParalivesAccess.Speech;
namespace UnityEngine {
 public static class Time {public static int frameCount;}
 public class GameObject {public bool activeInHierarchy=true;public object Component;public T GetComponent<T>()where T:class=>Component as T;public bool TryGetComponent<T>(out T c)where T:class{c=GetComponent<T>();return c!=null;}}
 public struct Vector2 {public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}public static float Distance(Vector2 a,Vector2 b)=>(float)Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y));public static implicit operator Vector2(Vector3 p)=>new Vector2(p.x,p.y);}
 public struct Vector3 {
  public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
  public static Vector3 zero=>default;public static Vector3 up=>new Vector3(0,1,0);public static Vector3 forward=>new Vector3(0,0,1);
  public float magnitude=>(float)Math.Sqrt(x*x+y*y+z*z);public Vector3 normalized=>magnitude>0?this/magnitude:zero;
  public static Vector3 operator +(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
  public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);public static Vector3 operator -(Vector3 a)=>zero-a;
  public static Vector3 operator *(Vector3 a,float b)=>new Vector3(a.x*b,a.y*b,a.z*b);public static Vector3 operator /(Vector3 a,float b)=>a*(1/b);
  public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;
  public static Vector3 Cross(Vector3 a,Vector3 b)=>new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
  public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;
 }
 public struct Quaternion {
  public float Yaw;public static Quaternion operatorIdentity=>new Quaternion();
  public static Quaternion LookRotation(Vector3 forward,Vector3 up)=>new Quaternion{Yaw=(float)Math.Atan2(forward.x,forward.z)};
  public static Vector3 operator *(Quaternion q,Vector3 v){float c=(float)Math.Cos(q.Yaw),s=(float)Math.Sin(q.Yaw);return new Vector3(c*v.x+s*v.z,v.y,-s*v.x+c*v.z);}
  public static float Angle(Quaternion a,Quaternion b)=>Math.Abs(a.Yaw-b.Yaw)*180/(float)Math.PI;
 }
 public class Transform {public Vector3 position;public Quaternion rotation;public Vector3 up=>rotation*Vector3.up;public Vector3 TransformPoint(Vector3 p)=>position+rotation*p;public Vector3 TransformDirection(Vector3 p)=>rotation*p;}
 public static class Mathf {public static float Abs(float n)=>Math.Abs(n);public static float Max(float a,float b)=>Math.Max(a,b);public static float Clamp01(float n)=>Clamp(n,0,1);public static float Clamp(float n,float a,float b)=>Math.Max(a,Math.Min(b,n));}
 public class Mesh {public Vector3[] vertices,normals;}
}
namespace UnityEngine.InputSystem {
 public enum Key {Home,End,Semicolon}
 public class KeyState {public bool wasPressedThisFrame,wasReleasedThisFrame,isPressed;}
 public class Keyboard {public static Keyboard current=new Keyboard();public Key? Pressed;public KeyState rightArrowKey=new KeyState(),leftArrowKey=new KeyState(),upArrowKey=new KeyState(),downArrowKey=new KeyState(),enterKey=new KeyState(),numpadEnterKey=new KeyState(),leftShiftKey=new KeyState(),rightShiftKey=new KeyState();}
 public class PointerControl {public Vector2 Value;public Vector2 ReadValue()=>Value;}
 public class Mouse {public static Mouse current=new Mouse();public PointerControl position=new PointerControl();public Vector2 Requested;public int Warps;public void WarpCursorPosition(Vector2 p){Requested=p;Warps++;}public void Arrive(){InputManager.Instance.Cursor=Requested;position.Value=Requested;}}
}
namespace HarmonyLib {
 public class Patch {public string owner="test";public MethodInfo PatchMethod;}
 public class Patches {public List<Patch> Prefixes=new List<Patch>(),Finalizers=new List<Patch>();}
 public class HarmonyMethod {public MethodInfo Method;public HarmonyMethod(MethodInfo m){Method=m;}}
 public class Harmony {public static Dictionary<MethodInfo,Patches> All=new Dictionary<MethodInfo,Patches>();public void Patch(MethodInfo m,HarmonyMethod prefix=null,HarmonyMethod finalizer=null){var p=new Patches();if(prefix!=null)p.Prefixes.Add(new Patch{PatchMethod=prefix.Method});if(finalizer!=null)p.Finalizers.Add(new Patch{PatchMethod=finalizer.Method});All[m]=p;}public static Patches GetPatchInfo(MethodInfo m)=>All.ContainsKey(m)?All[m]:null;}
 public static class AccessTools {public static MethodInfo Method(Type t,string n,Type[] p=null)=>p==null?t.GetMethod(n,BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic):t.GetMethod(n,BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,p,null);}
}
public class InputButton {public bool Down,Up,Pressed,Throw;public int Releases;public void SetValue(float v){Down=v>0.5f;Up=!Down;Pressed=false;if(Up)Releases++;if(Throw)throw new Exception("native callback failed");}public void Update(){if(Down){Down=false;Pressed=true;}if(Up){Up=false;Pressed=false;}}}
public enum GameStates {BuildMode,LiveMode}
public enum ColliderType {None,Wall,Floor,Terrain}
public enum WallVerticalSectionPartSides {SideA,SideB}
public class ItemCubeTransform {public Vector3 LocalPosition,FinalSize=new Vector3(1,1,0.2f);}
public class ItemOffset {public Vector3 Offset;}
public struct BoundingBox {public Vector3 Center;}
public class ItemObject {public ItemObjectRoot Root;}
public class ItemObjectRoot {public ItemObjectDoor Door;public bool SnapsToBottomOfWall;public T GetComponent<T>()where T:class=>Door as T;public int InstanceID=1,Layer,Price=50;public bool IsWallItem=true,IsCeilingItem;public GameObject gameObject=new GameObject();public ulong LotPlacedOnGUID=1;public ItemCubeTransform CubeTransform=new ItemCubeTransform();public Transform transform=new Transform();public ItemOffset ItemOffset;public Vector3 WallOffset;public SegmentWallSectionObject WallSectionPlacedOn;public int GetPrice()=>Price;public BoundingBox GetWorldBoundingBox()=>new BoundingBox{Center=transform.position};}
public class ItemObjectDoor {}
public class WallHeightManager {public static WallHeightManager Instance=new WallHeightManager();public int Height;public int GetFloorHeight(ulong lot,int cluster,int layer)=>Height;}
namespace Setting {public class CameraCurve {public float Evaluate(float t)=>0;}public class FreeCamera {public float MinZoomDistance=.5f,MaxZoomDistance=100,StartMinAngle=10,EndMinAngle=40,MaxAngle=85;public CameraCurve MinLookAngleByDistance=new CameraCurve();}public class Wall {public float StepHeight=0.5f;}}public static class Settings {public static T Get<T>()where T:new()=>new T();}
public class ItemObjectInPlacement {public ItemObjectRoot Item=new ItemObjectRoot();public object InventoryItem;public bool IsNewItem=true,CursorTeleported,FromRotationWidget,IsInOffset;public Vector3 CursorTeleportPosition,LocalMouseOffset,LastCollidedNormal;public GameObject LastCollidedObject;public ColliderType LastColliderType;}
public class Player {public int PlayerIndex,Layer;public GameStates State=GameStates.BuildMode;public bool PlayerActionsAreAllowed=true,PlayerActionBlockedByUIWindow;public ItemObjectInPlacement ItemInPlacement=new ItemObjectInPlacement();public object ItemInRotation;public int GetLotLayer(ulong lot)=>Layer;}
public class HybridCameraFree {public Transform LookTarget=new Transform();public float XAngleRatio,TargetDistance;public float TargetDistanceSpeed,CurrentExtraLookUpRatio;}
public class HybridCamera {public HybridCameraFree FreeCamera=new HybridCameraFree();}
public class HybridPlayer {public Player Player=new Player();public bool HasInputFieldFocused,IsUsingGamePad;public HybridCamera HybridCamera=new HybridCamera();public InputButton ButtonConfirm=new InputButton(),ButtonCancel=new InputButton(),ButtonUndo=new InputButton(),ButtonRedo=new InputButton();}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public class InputManager {public static InputManager Instance=new InputManager();public Vector2 Cursor;public bool OverUI;public Vector2 GetCursorPosition(int p)=>Cursor;public bool IsPointerOverUI(int p)=>OverUI;}
public class MoneyManager {public static MoneyManager Instance=new MoneyManager();public bool Affordable=true;public bool CanPurchase(int p)=>Affordable;}
public class SegmentObject {public object NodeA,NodeB;public int Cluster;public GameObject gameObject=new GameObject();public int InstanceID=11,Layer,PlacementPlayer=-1;public ulong LotPlacedOnGUID=1;public bool IsCurved;public SegmentObject ReplacingSegment;}
public class SegmentWallObject:SegmentObject {public List<SegmentWallSectionObject> WallSections=new List<SegmentWallSectionObject>();}
public class SegmentWallSectionObject {public SegmentWallSectionObject(){gameObject.Component=this;}public GameObject gameObject=new GameObject();public SegmentWallObject Wall;public int InstanceID=12,StartHeight;public bool IsFence,DisabledByUser,DisabledButVisible;public SegmentWallSectionRenderer SideA,SideB;}
public class SegmentWallSectionRenderer {public GameObject gameObject=new GameObject();public Transform transform=new Transform();public SegmentWallSectionObject WallSection;public WallVerticalSectionPartSides WallSide;public Mesh Mesh;}
public class ZoneSegment {public SegmentObject Segment;public bool FromAToB=true;}
public class ZoneObject {public GameObject gameObject=new GameObject();public int InstanceID=10,Layer;public ulong LotPlacedOnGUID=1;public float SurfaceArea=16;public List<ZoneSegment> Segments=new List<ZoneSegment>();}
public class ZoneWallObject:ZoneObject {}
public class ZoneManager {public static ZoneManager Instance=new ZoneManager();public List<ZoneObject> Zones=new List<ZoneObject>();public List<ZoneObject> GetLotZones(ulong lot)=>Zones;public HashSet<ZoneWallObject> DirtyWallZones=new HashSet<ZoneWallObject>();}
public class SegmentManager {public static SegmentManager Instance=new SegmentManager();public List<SegmentObject> Walls=new List<SegmentObject>();public List<SegmentObject> GetLotSegments(ulong lot)=>Walls;}
public static class BuildModeRefreshManager {public static bool IsZoneStateRefreshed=true;}
public struct RaycastHit {public Vector3 normal;}
public static class RaycastSystem {
 public static Vector3 BoxCenter;public static List<GameObject> Objects;
 public static (int,int,int,List<GameObject>) BoxCastAll(Vector3 center,Vector3 direction,Vector3 half,Quaternion rotation,float distance,bool includeTerrain,bool includeFloor,bool includeWalls){BoxCenter=center;return (0,0,0,Objects);}
 public static GameObject Hit;public static Vector3 Normal;public static ColliderType First=ColliderType.Wall,Second=ColliderType.Wall;
 public static (RaycastHit hit,ColliderType type,GameObject gameObject) ScreenCastAndGoThroughTerrain(Vector3 screen,bool includeTerrain=false,bool includeFloor=false,bool includeWalls=false,bool includeObjects=false,Vector3 rayOffset=default)=>(new RaycastHit{normal=Normal},First,Hit);
 public static (RaycastHit hit,ColliderType type,GameObject gameObject) ScreenCast(Vector3 screen,bool includeTerrain=false,bool includeFloor=false,bool includeWalls=false,bool includeObjects=false,Vector3 rayOffset=default)=>(new RaycastHit{normal=Normal},Second,Hit);
}
public class EndMoveItem {public void UpdateForPlayer(Player player){}}
public class UpdateMoveItem {public void UpdateForPlayer(Player player){}}
public static class FormatService {public static string GetMoney(int n)=>"money "+n;}
public static class MeasurementUnitService {public static string FormatSurfaceArea(float n)=>n+" square metres";}
namespace ParalivesAccess.Speech {public static class ScreenReader {public static List<string> Spoken=new List<string>();public static void Speak(string s){Spoken.Add(s);}public static string NormalizeForOutput(string s)=>s;}}
namespace ParalivesAccess {
 public class Logger {public List<string> Errors=new List<string>();public void LogInfo(string s){}public void LogError(string s){Errors.Add(s);}}
 public static class Plugin {public const string PluginGuid="test";public static Logger Log=new Logger();}
 public static class Loc {public static string Get(string k,params object[] args)=>k+"("+string.Join(";",args)+")";}
}
namespace ParalivesAccess.Handlers { public static class CeilingPlacementHandler {public static string Problem(HybridPlayer h,ItemObjectRoot r)=>null;}
 public static class RoofClearanceReader {public static string Problem(ItemObjectRoot r)=>null;}
 public static class PlacementSupportConsent {public static string FloorProblem(ItemObjectInPlacement p)=>null;public static string Problem(ItemObjectInPlacement p)=>null;}
 internal static class RoomSpatialReader {internal static string DescribeSide(WallFaceTarget f,WallFaceFrame frame=null)=>"wall side";internal static string LotSide(ulong lot,WallFaceFrame frame)=>"front wall";}
 public static class RoofConstructionHandler {public static bool CanOverlap(ItemObjectRoot a,ItemObjectRoot b)=>false;public static string Problem(HybridPlayer h,ItemObjectInPlacement p)=>null;public static bool IsRoof(ItemObjectRoot r)=>false;}
 public static class PlacementActionsHandler {public static bool Active=>false;}
 public static class FacingTargetHandler {public static string Problem(HybridPlayer h,ItemObjectRoot i)=>null;}
 public static class ItemAlignmentHandler {public static string Problem(HybridPlayer h,ItemObjectRoot i)=>null;}
 public static class RoomPlacementGrid {public static string Problem(ulong lot,int layer,Vector3 p)=>null;}
 public static class SlotPlacementHandler {public static bool Active=>false;public static bool AllowsLinkedOverlap(ItemObjectRoot a,ItemObjectRoot b)=>false;}
 public static class CoordinateEntryHandler {public static bool BlocksPlacement,Requested;public static Func<bool> Valid;public static Action<double,double> Apply;public static bool WasRequested(HybridPlayer h)=>Requested;public static void Open(HybridPlayer h,ItemObjectInPlacement p,string hint,double x,double z,Func<bool> valid,Action<double,double> apply){Valid=valid;Apply=apply;}}
 public static class ExistingItemEditHandler {public static bool MovesWith(ItemObjectRoot root,ItemObjectRoot candidate)=>false;}
 public static class ModInputKey {public static bool Available=true;public static bool WasPressed(Keyboard k,Key key)=>k?.Pressed==key;public static bool IsAvailable(HybridPlayer p,Key key,out string conflict){conflict="hotkey_conflict";return Available;}}
 public static class ItemDescription {public static string GetProductIdentity(ItemObjectRoot r)=>"Window";public static string GetName(ItemObjectRoot r)=>"Blocker";}
 internal static class PlacementGridReader {public static string Length(float n)=>n+" m";public static string Dimensions(ItemObjectRoot r)=>"dimensions";public static bool TryRead(ulong lot,float spacing,out PlacementGrid grid){grid=new PlacementGrid(0,0,0,spacing);return true;}public static string Position(PlacementGrid g,Vector3 p){g.Coordinates(p.x,p.z,out double x,out double y);return "grid column "+x+", row "+y;}}
 public static class WorldTargeting {public static bool Succeed=true;public static int Calls;public static bool FrameArea(Player p,ulong lot,int layer,Vector3 target,string reason){Calls++;return Succeed;}}
 public static class ItemPlacementHandler {
  public static bool Enabled=true,Visible=true;public static ulong Lot=1;public static float GridSpacingMetres=0.5f,CameraOffset;public static string Problem;
  public static ulong ResolvePlacementLot(Player p,out string source){source="camera";return Lot;}
  public static bool TryProjectTarget(HybridPlayer h,Vector3 world,out Vector2 screen){screen=new Vector2(500+world.x*19.37f+CameraOffset,300+world.y*19.37f);return Visible;}
  private static int _blockerCheckedFrame;private static ItemObjectRoot _blockerCheckedRoot,_blockingObject;
  private static string GetPlacementProblem(ItemObjectRoot r)=>Problem;
  private static bool IsInsideOwnedLot(ItemObjectRoot r)=>r.LotPlacedOnGUID==Lot&&Lot!=0;
  // Relationship filtering is unchanged. These fixtures exercise ordinary blockers and centre geometry.
  private static bool IsSemanticPlacementSupport(ItemObjectRoot r,ItemObjectInPlacement p,ItemObjectRoot candidate)=>false;
  PRODUCTION_GUARD
 }
 public static class WallItemChecks {
  static int checks;static HybridPlayer H=>PlayerManager.Instance.HybridPlayer1;static ItemObjectInPlacement P=>H.Player.ItemInPlacement;
  static SegmentWallObject Wall;static SegmentWallSectionObject Section;static ZoneWallObject Room;
  static Type Handler=>typeof(WallItemPlacementHandler);
  static object Get(string n)=>Handler.GetField(n,BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  static object Call(string n,params object[] args)=>Handler.GetMethod(n,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
  static bool Said(string key)=>ScreenReader.Spoken.Any(s=>s.Contains(key+"("));
  static void Check(bool ok,string name){if(!ok)throw new Exception(name+"; "+string.Join(" | ",ScreenReader.Spoken));checks++;}
  static bool Near(float a,float b)=>Math.Abs(a-b)<0.0001f;
  static Mesh Mesh(float width=6,float height=4,bool reverse=false)=>new Mesh{vertices=new[]{new Vector3(0,0,0),new Vector3(width,0,0),new Vector3(width,height,0),new Vector3(0,height,0)},normals=new[]{new Vector3(0,0,reverse?-1:1)}};
  static void Reset(){
   WallHeightManager.Instance.Height=0;
   CoordinateEntryHandler.BlocksPlacement=CoordinateEntryHandler.Requested=false;CoordinateEntryHandler.Valid=null;CoordinateEntryHandler.Apply=null;
   WallItemPlacementHandler.Clear();PlayerManager.Instance=new PlayerManager();InputManager.Instance=new InputManager();SegmentManager.Instance=new SegmentManager();ZoneManager.Instance=new ZoneManager();Keyboard.current=new Keyboard();Mouse.current=new Mouse();
   ItemPlacementHandler.Enabled=ItemPlacementHandler.Visible=WallItemPlacementHandler.Enabled=ModInputKey.Available=WorldTargeting.Succeed=true;ItemPlacementHandler.Lot=1;ItemPlacementHandler.CameraOffset=0;ItemPlacementHandler.Problem=null;WorldTargeting.Calls=0;BuildModeRefreshManager.IsZoneStateRefreshed=true;
   Wall=new SegmentWallObject();Section=new SegmentWallSectionObject{Wall=Wall};Wall.WallSections.Add(Section);
   Section.SideA=new SegmentWallSectionRenderer{WallSection=Section,Mesh=Mesh()};Section.SideB=new SegmentWallSectionRenderer{WallSection=Section,Mesh=Mesh(reverse:true),WallSide=WallVerticalSectionPartSides.SideB};
   Room=new ZoneWallObject();Room.Segments.Add(new ZoneSegment{Segment=Wall});ZoneManager.Instance.Zones.Add(Room);SegmentManager.Instance.Walls.Add(Wall);
   RaycastSystem.Hit=Section.SideA.gameObject;RaycastSystem.Normal=Vector3.forward;RaycastSystem.First=RaycastSystem.Second=ColliderType.Wall;RaycastSystem.Objects=null;MoneyManager.Instance=new MoneyManager();
   Time.frameCount++;ScreenReader.Spoken.Clear();
  }
  static void Next(){Time.frameCount++;Keyboard.current=new Keyboard();}
  static void Begin(){WallItemPlacementHandler.Begin(H,P,1);Next();}
  static void Before()=>WallItemPlacementHandler.BeforeGameSystems(H,P);
  static void Enter(){Keyboard.current.enterKey.wasReleasedThisFrame=true;Before();}
  static void Select(){Begin();Enter();Next();Enter();Check(H.ButtonConfirm.Releases==0&&Get("_stage").ToString()=="Position","room and face selection never purchases");Next();}
  static void Observe(bool desired=true){
   Call("BeforeNativeMove",H.Player);Mouse.current?.Arrive();P.CursorTeleported=false;
   if(desired){var f=(WallFaceFrame)Get("_frame");P.Item.CubeTransform.LocalPosition=f.Point((float)Get("_along"),(float)Get("_height"));P.LastCollidedNormal=f.Normal;P.LastCollidedObject=Section.gameObject;P.LastColliderType=ColliderType.Wall;P.Item.WallSectionPlacedOn=Section;}
   WallItemPlacementHandler.AfterGameSystems(H,P);Next();
  }
  static void Settle(){for(int i=0;i<3;i++){Before();Observe();}}
  static void End(){Call("BeforeNativeEnd",H.Player);Call("AfterNativeEnd",H.Player);}
  static void Ready(){Select();Settle();Check(Said("wall_item_ready"),"matching observed attachment and centre becomes ready");}
  static SegmentWallSectionObject Adjacent(){
   var wall=new SegmentWallObject{InstanceID=31,NodeA=Wall.NodeB=new object()};var section=new SegmentWallSectionObject{InstanceID=32,Wall=wall};wall.WallSections.Add(section);
   section.SideA=new SegmentWallSectionRenderer{WallSection=section,Mesh=Mesh()};section.SideA.transform.position=new Vector3(6,0,0);section.SideB=new SegmentWallSectionRenderer{WallSection=section,Mesh=Mesh(reverse:true),WallSide=WallVerticalSectionPartSides.SideB};section.SideB.transform.position=new Vector3(6,0,0);Room.Segments.Add(new ZoneSegment{Segment=wall});SegmentManager.Instance.Walls.Add(wall);return section;
  }
  public static string Run(){
   Check(WallItemPlacementHandler.ApplyPatches(new Harmony()),"native end, finalizer and precise movement hooks installed and verified");
   Reset();var groups=WallFaceReader.Read(1,0);Check(groups.Count==2&&groups[0].Faces.Count==1&&groups[1].Faces.Count==1,"room-facing side and remaining outside face are distinct groups");
   Check(groups[0].Faces[0].Face==Section.SideA&&groups[1].Faces[0].Face==Section.SideB,"native traversal chooses room side A");
   Room.Segments[0].FromAToB=false;groups=WallFaceReader.Read(1,0);Check(groups[0].Faces[0].Face==Section.SideB,"reverse native traversal chooses room side B");
   Reset();groups=WallFaceReader.Read(1,0);groups[0].Faces[0].TryRead(1,0,out var frame);Check(Near(frame.Width,6)&&Near(frame.Height,4)&&Near(frame.Along(new Vector3(1,2,0)),1),"front face measures from left edge with native dimensions");
   groups[1].Faces[0].TryRead(1,0,out var reverse);Check(Near(reverse.Along(new Vector3(1,2,0)),5),"opposite face reverses left and right");
   Section.SideA.transform.position=new Vector3(100,7,-40);Section.SideA.transform.rotation=new Quaternion{Yaw=0.7f};groups[0].Faces[0].TryRead(1,0,out var rotated);var point=rotated.Point(2,1);Check(Math.Abs(rotated.Along(point)-2)<0.0001f&&Near(rotated.AboveBottom(point),1),"translated rotated face has stable physical coordinates");
   Check(!frame.Same(rotated),"changed frame invalidates reviewed geometry");
   Check(!WallFaceFrame.TryCreate(new[]{new Vector3(0,0,0),new Vector3(6,0,0),new Vector3(3,4,0),new Vector3(0,4,0)},Vector3.forward,out _),"sloped or nonrectangular mesh rejected");
   Check(!WallFaceFrame.TryCreate(new[]{new Vector3(0,0,0),new Vector3(6,0,0),new Vector3(6,4,0),new Vector3(6,4,0)},Vector3.forward,out _),"missing rectangle corner is not treated as a face");
   Check(!WallFaceFrame.TryCreate(Mesh().vertices,Vector3.up,out _),"horizontal top face rejected");
   var bad=Mesh().vertices;bad[0].x=float.NaN;Check(!WallFaceFrame.TryCreate(bad,Vector3.forward,out _),"nonfinite mesh rejected");
   Reset();Wall.IsCurved=true;Check(WallFaceReader.Read(1,0).Count==0,"curved walls excluded");
   Reset();Section.IsFence=true;Check(WallFaceReader.Read(1,0).Count==0,"fences excluded");
   Reset();Section.DisabledByUser=true;Check(WallFaceReader.Read(1,0).Count==0,"disabled wall excluded");
   Reset();Section.DisabledButVisible=true;Check(WallFaceReader.Read(1,0).Count==0,"preview wall section excluded");
   Reset();Wall.PlacementPlayer=0;Check(WallFaceReader.Read(1,0).Count==0,"uncommitted construction preview excluded");
   Reset();Wall.Layer=1;Check(WallFaceReader.Read(1,0).Count==0,"another layer excluded");
   Reset();Wall.LotPlacedOnGUID=9;Check(WallFaceReader.Read(1,0).Count==0,"another lot excluded");
   Reset();var captured=WallFaceReader.Read(1,0)[0].Faces[0];Room.InstanceID++;Check(!captured.TryRead(1,0,out _),"reused room identity rejected");
   Reset();captured=WallFaceReader.Read(1,0)[0].Faces[0];Section.InstanceID++;Check(!captured.TryRead(1,0,out _),"reused section identity rejected");
   Reset();captured=WallFaceReader.Read(1,0)[0].Faces[0];ZoneManager.Instance.Zones.Clear();Check(!captured.TryRead(1,0,out _),"removed room membership rejected");
   Reset();captured=WallFaceReader.Read(1,0)[0].Faces[0];Section.SideA.Mesh=null;Check(!captured.TryRead(1,0,out _),"missing current native mesh rejected");
   Reset();Begin();Check(Said("wall_item_open")&&Said("wall_item_group"),"opening describes room selection");
   Enter();Check(WallItemPlacementHandler.Describe(true).Contains("grid column 0, row 0")&&WallItemPlacementHandler.Describe(true).Contains("grid column 12, row 0"),"face description distinguishes equal-sized walls by lot-grid endpoints");
   Reset();Ready();Enter();Check(H.ButtonConfirm.Releases==0,"Enter intent does not inject before native end");End();Check(H.ButtonConfirm.Releases==1&&!H.ButtonConfirm.Up&&!H.ButtonConfirm.Down&&!H.ButtonConfirm.Pressed,"one confirm release is scoped to native end and cleaned");
   Call("BeforeNativeEnd",H.Player);Check(H.ButtonConfirm.Releases==1,"same-frame end reentry cannot submit twice");
   WallItemPlacementHandler.AfterGameSystems(H,P);Check(Said("wall_item_not_placed"),"native no-op explicitly retains preview without claiming success");
   Reset();Select();Enter();End();Check(H.ButtonConfirm.Releases==0,"unobserved preview cannot be purchased");
   Reset();Ready();Enter();P.Item.Price++;End();Check(H.ButtonConfirm.Releases==0,"price change at native boundary requires new observation");
   Reset();Ready();Enter();P.Item.CubeTransform.LocalPosition+=new Vector3(0.2f,0,0);End();Check(H.ButtonConfirm.Releases==0,"late native movement invalidates confirmation");
   Reset();Ready();Enter();Section.SideA.Mesh=Mesh(7,4);End();Check(H.ButtonConfirm.Releases==0,"native face reshape invalidates confirmation");
   Reset();Ready();Enter();RaycastSystem.Hit=Section.SideB.gameObject;End();Check(H.ButtonConfirm.Releases==0&&Said("wall_item_wrong_face"),"same wall opposite raycast side refused");
   Reset();Ready();Enter();RaycastSystem.First=ColliderType.Terrain;End();Check(H.ButtonConfirm.Releases==0,"terrain blocks the native wall ray");
   Reset();Ready();Enter();H.HasInputFieldFocused=true;End();Check(H.ButtonConfirm.Releases==0,"late text ownership prevents purchase");
   Reset();Ready();Enter();H.Player.PlayerActionBlockedByUIWindow=true;End();Check(H.ButtonConfirm.Releases==0,"late native modal prevents purchase");
   Reset();Ready();Enter();H.IsUsingGamePad=true;End();Check(H.ButtonConfirm.Releases==0,"gamepad priority prevents purchase");
   Reset();Ready();Enter();ItemPlacementHandler.Problem="native validity error";End();Check(H.ButtonConfirm.Releases==0&&Said("placement_invalid"),"native validity guard checked at transaction boundary");
   Reset();Ready();Enter();RaycastSystem.Objects=new List<GameObject>{new GameObject{Component=new ItemObjectRoot()}};End();Check(H.ButtonConfirm.Releases==0&&Said("placement_occupied_move"),"new ordinary blocker at purchase boundary refuses");
   Reset();Ready();Enter();MoneyManager.Instance.Affordable=false;End();Check(H.ButtonConfirm.Releases==0&&Said("wall_item_unaffordable"),"changed funds refuse at purchase boundary");
   Reset();Ready();P.InventoryItem=new object();MoneyManager.Instance.Affordable=false;Enter();End();Check(H.ButtonConfirm.Releases==1,"native inventory item is not subjected to a new-purchase funds check");
   Reset();Begin();CoordinateEntryHandler.Requested=true;Before();Check(CoordinateEntryHandler.Apply==null&&Said("coordinate_choose_face"),"wall coordinate form requires chosen face");
   Reset();Ready();CoordinateEntryHandler.Requested=true;Before();Check(CoordinateEntryHandler.Valid()&&H.ButtonConfirm.Releases==0,"wall coordinate form captures selected live face without placing");CoordinateEntryHandler.Requested=false;CoordinateEntryHandler.Apply(2.25,1.75);Check(Near((float)Get("_along"),2.25f)&&Near((float)Get("_height"),1.75f)&&H.ButtonConfirm.Releases==0,"wall numeric coordinates preserve fractional distance and height");Settle();Enter();End();Check(H.ButtonConfirm.Releases==1,"wall numeric position needs settled native observation and separate confirm");
   Reset();Ready();CoordinateEntryHandler.Requested=true;Before();ItemPlacementHandler.Lot=2;Check(!CoordinateEntryHandler.Valid(),"changed lot invalidates wall coordinate form");ItemPlacementHandler.Lot=1;H.Player.Layer=1;Check(!CoordinateEntryHandler.Valid(),"changed floor invalidates wall coordinate form");H.Player.Layer=0;Section.SideA.Mesh=Mesh(7,4);Check(!CoordinateEntryHandler.Valid(),"reshaped wall invalidates coordinate form");
   Reset();Ready();Enter();CoordinateEntryHandler.BlocksPlacement=true;End();Check(H.ButtonConfirm.Releases==0,"late coordinate text ownership blocks wall confirmation");
   Reset();P.IsNewItem=false;MoneyManager.Instance.Affordable=false;Ready();Enter();End();Check(H.ButtonConfirm.Releases==1,"existing wall item uses verified face targeting without repurchase funds");
   Reset();Ready();Enter();InputManager.Instance.OverUI=true;End();Check(H.ButtonConfirm.Releases==0&&Said("placement_pointer_over_ui"),"late pointer over native UI refuses purchase");
   Reset();Ready();Enter();H.ButtonConfirm.Down=true;End();Check(H.ButtonConfirm.Down&&H.ButtonConfirm.Releases==0,"real native confirm is not replaced or cleaned by accessibility bridge");
   Reset();Ready();Enter();Next();Call("BeforeNativeEnd",H.Player);Check(H.ButtonConfirm.Releases==0,"expired Enter cannot submit in a later frame");
   Reset();Ready();Enter();H.ButtonConfirm.Throw=true;End();Check(!H.ButtonConfirm.Up&&!WallItemPlacementHandler.Enabled&&Said("wall_item_unavailable"),"throwing input callback clears synthetic release before original native method");
   Reset();Ready();Enter();Call("BeforeNativeEnd",H.Player);Check(H.ButtonConfirm.Up,"native end sees synthetic Up");Call("AfterNativeEnd",H.Player);Check(!H.ButtonConfirm.Up&&H.ButtonConfirm.Releases==1,"native exception finalizer clears release without another OnUp");
   Reset();Ready();Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Before();Check(Near((float)Get("_along"),3.5f),"right moves half a metre along face");Observe();
   Keyboard.current.upArrowKey.wasPressedThisFrame=true;Keyboard.current.leftShiftKey.isPressed=true;Before();Check(Near((float)Get("_height"),2.1f),"Shift up moves one tenth metre vertically");Observe();
   Keyboard.current.Pressed=Key.End;Before();Check(Near((float)Get("_along"),3)&&Near((float)Get("_height"),2.1f),"centering preserves chosen height");
   Reset();Ready();Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Keyboard.current.enterKey.wasReleasedThisFrame=true;Before();End();Check(H.ButtonConfirm.Releases==0,"movement and Enter in one frame cannot place old preview");
   Reset();Ready();ItemPlacementHandler.CameraOffset=1;Enter();End();Check(H.ButtonConfirm.Releases==0,"camera reprojection invalidates settled preview");
   Reset();Ready();InputManager.Instance.Cursor=new Vector2(-100,-100);Before();Check(Get("_stage").ToString()=="Position"&&!Said("wall_item_manual"),"delayed legacy cursor alone does not release a native wall target");
   Mouse.current.position.Value=InputManager.Instance.Cursor;Before();Call("BeforeNativeMove",H.Player);Check(Get("_stage").ToString()=="Faces"&&!P.CursorTeleported&&Said("wall_item_manual"),"physical cursor movement releases precise targeting");
   Reset();Ready();P.LastCollidedNormal=-Vector3.forward;Observe(false);Before();Observe(false);Enter();End();Check(H.ButtonConfirm.Releases==0&&Said("wall_item_wrong_face"),"settled attachment to opposite normal refused");
   Reset();Ready();P.Item.CubeTransform.LocalPosition+=new Vector3(0.02f,0,0);Observe(false);Before();Observe(false);Enter();End();Check(H.ButtonConfirm.Releases==0&&Said("wall_item_alignment_changed"),"settled native snap drift cannot be called centred");
   var accepted=P.Item.CubeTransform.LocalPosition;WallItemPlacementHandler.AcceptActual(H,P);Check(Near((float)Get("_along"),accepted.x)&&Near((float)Get("_height"),accepted.y)&&H.ButtonConfirm.Releases==0,"actual-coordinate acceptance reads observed wall coordinates without placing");
   Reset();Ready();P.LastCollidedNormal=-Vector3.forward;WallItemPlacementHandler.AcceptActual(H,P);Observe(false);Before();Observe(false);Enter();End();Check(H.ButtonConfirm.Releases==0&&Said("wall_item_wrong_face"),"accepting coordinates retains native wall-face validation");
   Reset();Ready();ItemPlacementHandler.Visible=false;Enter();End();Check(H.ButtonConfirm.Releases==0&&Said("wall_item_out_of_view"),"offscreen request cannot confirm old position");
   Reset();Ready();Keyboard.current.Pressed=Key.Home;ModInputKey.Available=false;int framed=WorldTargeting.Calls;Before();Check(WorldTargeting.Calls==framed&&ScreenReader.Spoken.Contains("hotkey_conflict"),"rebound Home is respected");
   Reset();Ready();H.Player.Layer=1;Before();Check(Get("_stage").ToString()=="Rooms"&&Said("wall_item_context_changed"),"layer change restarts explicit target selection");
   Reset();Ready();ItemPlacementHandler.Lot=9;Before();Check(Get("_stage").ToString()=="Rooms"&&Said("wall_item_no_faces"),"lot change cannot retain prior wall");
   Reset();Ready();Keyboard.current.Pressed=Key.Semicolon;Before();Check(Get("_stage").ToString()=="Faces","semicolon leaves position for face list");Next();Keyboard.current.Pressed=Key.Semicolon;Before();Check(Get("_stage").ToString()=="Rooms","semicolon leaves face list for room list");
   Reset();Ready();P.Item.CubeTransform.FinalSize=new Vector3(8,1,0.2f);Enter();End();Check(H.ButtonConfirm.Releases==0&&Said("wall_item_outside"),"resized object wider than face refused");
   Reset();Ready();Enter();ZoneManager.Instance.DirtyWallZones.Add(Room);End();Check(H.ButtonConfirm.Releases==0,"dirty native room state prevents purchase");
   Reset();Ready();Check(WallItemPlacementHandler.Describe().Contains("wall_item_move")&&!WallItemPlacementHandler.Describe().Contains("money 50")&&!WallItemPlacementHandler.Describe().Contains("wall_item_requested"),"routine feedback retains actual position without unchanged requests or repeated price");
   Check(WallItemPlacementHandler.Describe(true).Contains("wall_item_position")&&WallItemPlacementHandler.Describe(true).Contains("money 50"),"detailed review retains centre height dimensions and price");
   P.Item.CubeTransform.LocalPosition+=new Vector3(.2f,0,0);Observe(false);Check(WallItemPlacementHandler.Describe().Contains("wall_item_requested"),"requested coordinates are supplied when actual position differs");
   Reset();Ready();ScreenReader.Spoken.Clear();P.Item.Price=65;Settle();Check(Said("wall_item_price_changed")&&ScreenReader.Spoken.Any(s=>s.Contains("money 65")),"changed price is announced during ordinary wall feedback");ScreenReader.Spoken.Clear();Settle();Check(!Said("wall_item_price_changed"),"unchanged price is not repeated while idle");
   Check(WallItemPlacementHandler.DescribeResult(P.Item).Contains("wall_item_result("),"result checks actual native wall reference and requested centre");P.Item.WallSectionPlacedOn=null;Check(WallItemPlacementHandler.DescribeResult(P.Item).Contains("wall_item_result_unverified"),"missing post-purchase attachment cannot be called verified");
   Reset();Select();var f=(WallFaceFrame)Get("_frame");P.Item.ItemOffset=new ItemOffset{Offset=new Vector3(0.3f,0.4f,0.5f)};P.Item.WallOffset=new Vector3(0.1f,0.2f,0.3f);P.LocalMouseOffset=new Vector3(0.7f,0.8f,9);
   var aim=WallItemPlacementHandler.AimPoint(P,f,3,2);Check(Near(aim.x,2.5f)&&Near(aim.y,1.4f)&&Near(aim.z,0.3f),"native item mouse depth and wall offsets affect the aim without modifying the preview");
   Check(Near(P.Item.CubeTransform.LocalPosition.x,0),"aim calculation never writes native item position");
   Reset();P.Item.CubeTransform.LocalPosition=new Vector3(2,1,0);P.Item.transform.position=new Vector3(2,1,0);ItemPlacementHandler.SemanticPlacementProblem(H,P.Item);Check(Near(RaycastSystem.BoxCenter.y,1),"wall occupancy uses native centred bounds without a floor-item half-height shift");
   P.Item.IsWallItem=false;ItemPlacementHandler.SemanticPlacementProblem(H,P.Item);Check(Near(RaycastSystem.BoxCenter.y,1.5f),"ordinary floor-item occupancy keeps its existing centre convention");
   Reset();Ready();P.Item.CubeTransform.LocalPosition=new Vector3(float.NaN,0,0);Check(WallItemPlacementHandler.DescribeResult(P.Item).Contains("wall_item_result_unverified"),"nonfinite native result cannot verify selected coordinates");
   Reset();Ready();var exact=(Vector2)Get("_screen");InputManager.Instance.Cursor=new Vector2((float)Math.Round(exact.x),(float)Math.Round(exact.y));Before();Call("BeforeNativeMove",H.Player);Check(P.CursorTeleported&&Near(P.CursorTeleportPosition.x,exact.x)&&Near(P.CursorTeleportPosition.y,exact.y),"idle native movement preserves fractional pixel target despite rounded mouse cursor");
   Reset();var adjacent=Adjacent();Ready();Check(WallItemPlacementHandler.CentreWholeWall(H,P).Contains("wall_span_selected")&&Near((float)Get("_along"),6)&&Near(((WallFaceFrame)Get("_frame")).Width,12),"whole-wall centre spans both native sections");Check(H.ButtonConfirm.Releases==0,"whole-wall alignment only requests a preview");Settle();Enter();End();Check(H.ButtonConfirm.Releases==1,"centre on native seam can confirm after observed attachment");Check(WallItemPlacementHandler.DescribeResult(P.Item).Contains("wall_span_result"),"whole-wall result names combined coordinate scope");
   Reset();adjacent=Adjacent();Ready();WallItemPlacementHandler.CentreWholeWall(H,P);Settle();P.LastCollidedObject=adjacent.gameObject;P.Item.WallSectionPlacedOn=adjacent;Observe(false);Before();Observe(false);RaycastSystem.Hit=adjacent.SideA.gameObject;Enter();End();Check(H.ButtonConfirm.Releases==1,"either member may own exact native seam attachment");
   Reset();adjacent=Adjacent();Ready();WallItemPlacementHandler.CentreWholeWall(H,P);Settle();RaycastSystem.Hit=adjacent.SideB.gameObject;RaycastSystem.Normal=-Vector3.forward;Enter();End();Check(H.ButtonConfirm.Releases==0,"opposite side of joined wall remains invalid");
   Reset();adjacent=Adjacent();Ready();WallItemPlacementHandler.CentreWholeWall(H,P);Settle();Enter();adjacent.SideA.transform.position=new Vector3(6.1f,0,0);End();Check(H.ButtonConfirm.Releases==0,"gap introduced after review invalidates whole wall");
   Reset();adjacent=Adjacent();Ready();WallItemPlacementHandler.OpenDivisions(H,P);Check(Near((float)Get("_along"),3)&&CoordinateEntryHandler.Valid(),"spacing form joins frame without first centring old request");CoordinateEntryHandler.Apply(3,3);Check(Near((float)Get("_along"),9)&&H.ButtonConfirm.Releases==0,"third of three centres uses entire twelve-metre wall");CoordinateEntryHandler.Apply(4,3);Check(Near((float)Get("_along"),9)&&Said("wall_span_division_invalid"),"invalid division leaves last request unchanged");
   Reset();P.Item.Door=new ItemObjectDoor();P.Item.SnapsToBottomOfWall=true;Ready();Check(Near((float)Get("_height"),P.Item.CubeTransform.FinalSize.y/2),"door selection starts at native floor height rather than wall centre");Check(WallItemPlacementHandler.Describe(true).Contains("door_threshold(0 m"),"F3 includes measured doorway threshold gap");P.Item.CubeTransform.LocalPosition+=Vector3.up;Observe(false);Before();Observe(false);Enter();End();Check(H.ButtonConfirm.Releases==0&&Said("wall_item_alignment_changed"),"automatic door height differing from request cannot confirm");
   Reset();Ready();Check(WallItemPlacementHandler.AlignDoorFloor(H,P).Contains("door_floor_unknown"),"ordinary wall item does not get guessed doorway command");P.Item.Door=new ItemObjectDoor();Check(WallItemPlacementHandler.AlignDoorFloor(H,P).Contains("door_floor_requested")&&Near((float)Get("_height"),0.5f)&&H.ButtonConfirm.Releases==0,"door floor command changes only reviewed target height");
   Reset();P.Item.Door=new ItemObjectDoor();WallHeightManager.Instance.Height=4;Section.SideA.transform.position=Section.SideB.transform.position=new Vector3(0,2,0);Ready();Check(Near(((WallFaceFrame)Get("_frame")).Origin.y+(float)Get("_height"),2.5f),"raised native floor uses actual world elevation");
   Reset();P.Item.SnapsToBottomOfWall=true;Ready();Check(P.Item.Door==null&&Near((float)Get("_height"),P.Item.CubeTransform.FinalSize.y/2),"floor-anchored sink follows native bottom snapping even without door component");
   Check(WallItemPlacementHandler.Describe(true).Contains("wall_item_floor_height"),"fixed native height is explained in detailed review");
   Keyboard.current.upArrowKey.wasPressedThisFrame=true;Before();Next();Check(Near((float)Get("_height"),P.Item.CubeTransform.FinalSize.y/2),"vertical arrow cannot request impossible height for native bottom snapping");
   P.Item.WallOffset=new Vector3(0,.8f,0);var bottomFrame=(WallFaceFrame)Get("_frame");Check(WallItemPlacementHandler.AimPoint(P,bottomFrame,2,.4f).y>bottomFrame.Origin.y,"bottom-snapped sink ray remains above the floor despite wall offset");
   P.Item.CubeTransform.FinalSize=new Vector3(.7f,.8f,.9f);var sinkAim=WallItemPlacementHandler.AimPoint(P,bottomFrame,2,.4f);Check(sinkAim.y>bottomFrame.Origin.y+bottomFrame.Height*.25f&&sinkAim.y<bottomFrame.Origin.y+bottomFrame.Height*.85f,"deep floor-anchored fixture keeps ray away from both wall edges before native rotation settles");
   P.Item.Door=new ItemObjectDoor();Check(Near(WallItemPlacementHandler.AimPoint(P,bottomFrame,2,.4f).y,bottomFrame.Origin.y+bottomFrame.Height-.05f),"floor-anchored door aims above its temporary opening while native height remains fixed");
   return checks+" wall-item face, joined-wall spacing, door and confirmation checks passed; native raycasts, purchase, attachment persistence and speech remain runtime checks.";
  }
 }
}
'@
$adapters = $adapters.Replace('PRODUCTION_GUARD', ($guard -join "`n"))
$sources += Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomNameAdapter.cs')
$sources += Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'fixtures/RoomBoundaryAdapter.cs')
Add-Type -TypeDefinition ($adapters + "`n" + ($sources -join "`n")) -Language CSharp
[ParalivesAccess.Handlers.WallItemChecks]::Run()
