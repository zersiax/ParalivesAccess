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
 public static class Time { public static int frameCount; }
 public class GameObject { public object Component; public T GetComponent<T>() where T:class => Component as T; public bool TryGetComponent<T>(out T c) where T:class { c=GetComponent<T>();return c!=null; } }
 public struct Vector2 { public float x,y; public Vector2(float x,float y){this.x=x;this.y=y;} public static float Distance(Vector2 a,Vector2 b)=>(float)Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y));public static implicit operator Vector2(Vector3 p)=>new Vector2(p.x,p.y); }
 public struct Vector3 {
  public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
  public static Vector3 zero=>default; public static Vector3 up=>new Vector3(0,1,0);
  public static Vector3 operator +(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
  public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);public static Vector3 operator -(Vector3 a)=>zero-a;
  public static Vector3 operator *(Vector3 a,float b)=>new Vector3(a.x*b,a.y*b,a.z*b);public static Vector3 operator /(Vector3 a,float b)=>a*(1/b);
  public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;public static float Distance(Vector3 a,Vector3 b){var d=a-b;return (float)Math.Sqrt(Dot(d,d));}
 }
 public struct Quaternion {public float Yaw;public static Vector3 operator *(Quaternion q,Vector3 v){float c=(float)Math.Cos(q.Yaw),s=(float)Math.Sin(q.Yaw);return new Vector3(c*v.x+s*v.z,v.y,-s*v.x+c*v.z);} public static float Angle(Quaternion a,Quaternion b)=>Math.Abs(a.Yaw-b.Yaw)*180/(float)Math.PI;}
 public class Transform {public Vector3 position;public Quaternion rotation; public Vector3 up=>Vector3.up;public Vector3 eulerAngles=>new Vector3(0,rotation.Yaw*180/(float)Math.PI,0);}
 public static class Mathf {public static float Abs(float n)=>Math.Abs(n);}
}
namespace UnityEngine.InputSystem {
 public enum Key {Semicolon,Home}
 public class KeyState {public bool wasPressedThisFrame,wasReleasedThisFrame,isPressed;}
 public class Keyboard {public static Keyboard current=new Keyboard();public Key? Pressed;public KeyState rightArrowKey=new KeyState(),leftArrowKey=new KeyState(),upArrowKey=new KeyState(),downArrowKey=new KeyState(),enterKey=new KeyState(),numpadEnterKey=new KeyState(),leftShiftKey=new KeyState(),rightShiftKey=new KeyState(),leftAltKey=new KeyState(),rightAltKey=new KeyState(),leftCtrlKey=new KeyState(),rightCtrlKey=new KeyState();}
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
namespace Setting {
 public static class Settings {public static T Get<T>()where T:new()=>Values<T>.Value;public static class Values<T>where T:new(){public static T Value=new T();}}
 public class CatalogueItem {public int[] Tag=new[]{1};}
 public class Items {public CatalogueItem Current=new CatalogueItem();public CatalogueItem GetItemByGUID(ulong id)=>Current;}
 public class BuildModeCatalogTags {public int Calls;public bool CompareTagsToTags(int[] a,int[] b){Calls++;return a!=null&&b!=null&&a.Intersect(b).Any();}}
 public class SnappingAndAlignment {public float SnapDistanceWithPlacementSlots=0.4f;}
}
public class InputButton {public bool Down,Up,Pressed,Throw;public int Releases;public void SetValue(float v){Down=v>0.5f;Up=!Down;Pressed=false;if(Up)Releases++;if(Throw)throw new Exception("native OnUp failed");}public void Update(){if(Down){Down=false;Pressed=true;}if(Up){Up=false;Pressed=false;}}}
public enum GameStates {BuildMode,LiveMode}
public enum ColliderType {None,Wall,Floor,Terrain,Object}
public class ItemCubeTransform {public Vector3 LocalPosition,FinalSize=new Vector3(1,1,1);}
public class ItemOffset {public Vector3 Offset;}
public struct BoundingBox {public Vector3 Center;}
public class ItemObject {public ItemObjectRoot Root;}
public class ItemObjectRoot {
 public int InstanceID=1,Layer,Price=50;public ulong CatalogueGUID=9,CurrentSkinGUID,LotPlacedOnGUID=1,PlacementSlotPlacedOn,ChairSlotPlacedOn;
 public bool IsWallItem,IsCeilingItem,IsLotMovementItem,isActiveAndEnabled=true,ItemCanStackOnOther=true,ItemCanBeStackedOn=true,SurfaceCanBeNotFlat,AnyItemCanBeStackedOn;
 public string Name="Computer";public GameObject gameObject=new GameObject();public ItemCubeTransform CubeTransform=new ItemCubeTransform();public Transform transform=new Transform();public ItemOffset ItemOffset;
 public ItemObjectRoot ItemPlacedOn,ItemAttachedTo;public List<ItemObjectRoot> ItemsOnThis=new List<ItemObjectRoot>(),ItemsAttachedToThis=new List<ItemObjectRoot>();
 public Dictionary<ulong,ItemObjectPlacementSlot> ItemPlacementSlots=new Dictionary<ulong,ItemObjectPlacementSlot>();public Dictionary<ulong,ItemObjectChairSlot> ItemChairSlots=new Dictionary<ulong,ItemObjectChairSlot>();
 public int GetPrice()=>Price;public BoundingBox GetWorldBoundingBox()=>new BoundingBox{Center=transform.position};
 public ItemObjectRoot(){gameObject.Component=this;}
}
public class ItemObjectPlacementSlot {public ulong GUID=10;public bool isActiveAndEnabled=true,PlacementSlotForAnimation,Compatible=true;public int ItemOnInstanceID=-1,TagCalls;public ItemObject Item=new ItemObject();public Transform transform=new Transform();public ItemObjectChairSlot ChairSlotLinked;public bool CompareValidTags(int[] tags){TagCalls++;return Compatible&&tags!=null;}}
public class ItemObjectChairSlot {public ulong GUID=11;public bool isActiveAndEnabled=true;public int ItemInSlotInstanceID=-1;public ItemObject Item=new ItemObject();public Transform transform=new Transform();public List<int> ChairTags=new List<int>{1};}
public class AssetLot {public List<ItemObjectRoot> LotItems=new List<ItemObjectRoot>();public List<ItemObjectPlacementSlot> ItemPlacementSlots=new List<ItemObjectPlacementSlot>();public List<ItemObjectChairSlot> ItemChairSlots=new List<ItemObjectChairSlot>();}
public class LotManager {public static LotManager Instance=new LotManager();public AssetLot Lot=new AssetLot();public AssetLot GetLotByGUID(ulong id)=>id==1?Lot:null;}
public class ItemObjectInPlacement {public ItemObjectRoot Item=new ItemObjectRoot();public object InventoryItem;public bool IsNewItem=true,CursorTeleported,FromRotationWidget,IsInOffset;public Vector3 CursorTeleportPosition,LocalMouseOffset;public GameObject LastCollidedObject;public ColliderType LastColliderType;}
public class Player {public int PlayerIndex,Layer;public GameStates State=GameStates.BuildMode;public bool PlayerActionsAreAllowed=true,PlayerActionBlockedByUIWindow,HasSnap;public ItemObjectInPlacement ItemInPlacement=new ItemObjectInPlacement();public object ItemInRotation;public int GetLotLayer(ulong lot)=>Layer;}
public class HybridPlayer {public Player Player=new Player();public bool HasInputFieldFocused,IsUsingGamePad;public InputButton ButtonConfirm=new InputButton(),ButtonCancel=new InputButton(),ButtonUndo=new InputButton(),ButtonRedo=new InputButton();}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public class InputManager {public static InputManager Instance=new InputManager();public Vector2 Cursor;public bool OverUI;public Vector2 GetCursorPosition(int p)=>Cursor;public bool IsPointerOverUI(int p)=>OverUI;}
public class MoneyManager {public static MoneyManager Instance=new MoneyManager();public bool Affordable=true;public bool CanPurchase(int p)=>Affordable;}
public class ZoneManager {public static ZoneManager Instance=new ZoneManager();public HashSet<int> DirtyWallZones=new HashSet<int>();}
public static class BuildModeRefreshManager {public static bool IsZoneStateRefreshed=true;}
public struct RaycastHit {public Vector3 normal,point;public float distance;}
public static class RaycastSystem {
 public static Vector3 BoxCenter,RayOffset;public static List<GameObject> Objects;
 public static (int,int,int,List<GameObject>) BoxCastAll(Vector3 center,Vector3 direction,Vector3 half,Quaternion rotation,float distance,bool includeTerrain,bool includeFloor,bool includeWalls){BoxCenter=center;return (0,0,0,Objects);}
 public static (RaycastHit hit,ColliderType type,GameObject gameObject) Floor;
 public static List<(RaycastHit hit,ColliderType type,GameObject gameObject)> Surface=new List<(RaycastHit,ColliderType,GameObject)>();
 public static (RaycastHit hit,ColliderType type,GameObject gameObject) ScreenCastAndGoThroughTerrain(Vector3 p,bool includeTerrain=false,bool includeFloor=false,Vector3 rayOffset=default){RayOffset=rayOffset;return Floor;}
 public static List<(RaycastHit hit,ColliderType type,GameObject gameObject)> ScreenCastAll(Vector3 p,bool includeObjects=false,Vector3 rayOffset=default,float distance=1000f)=>Surface.Where(h=>h.hit.distance<distance).ToList();
}
public class EndMoveItem {public void UpdateForPlayer(Player player){}}
public class UpdateMoveItem {public void UpdateForPlayer(Player player){}}
public static class FormatService {public static string GetMoney(int n)=>"money "+n;}
namespace ParalivesAccess.Speech {public static class ScreenReader {public static List<string> Spoken=new List<string>();public static void Speak(string s){Spoken.Add(s);}}}
namespace ParalivesAccess {
 public class Logger {public void LogInfo(string s){}public void LogError(string s){}}
 public static class Plugin {public const string PluginGuid="test";public static Logger Log=new Logger();}
 public static class Loc {public static string Get(string k,params object[] args)=>k+"("+string.Join(";",args)+")";}
}
namespace ParalivesAccess.Handlers { public static class CeilingPlacementHandler {public static bool Enabled;public static string Result(ItemObjectRoot r)=>"ceiling";public static string Problem(HybridPlayer h,ItemObjectRoot r)=>null;public static void OpenRooms(HybridPlayer h){}public static void Centre(HybridPlayer h){}public static void AcceptActual(HybridPlayer h,ItemObjectInPlacement p){}}
 public static class RoofClearanceReader {public static string Problem(ItemObjectRoot r)=>null;}
 public static partial class PlacementSupportConsent {public static string FloorProblem(ItemObjectInPlacement p)=>null;public static string Problem(ItemObjectInPlacement p)=>null;}
 public static class PlacementLocationReader {public static string Target(ItemObjectRoot r,ItemObjectRoot from=null)=>"target location";public static string Brief(ulong lot,int layer,Vector3 point)=>"target location";}
 public static partial class RoofConstructionHandler {public static bool CanOverlap(ItemObjectRoot a,ItemObjectRoot b)=>false;public static string Problem(HybridPlayer h,ItemObjectInPlacement p)=>null;public static bool IsRoof(ItemObjectRoot r)=>false;}
 // EDITING_STUBS_START
 public static class CoordinateEntryHandler {public static bool BlocksPlacement=>false;public static bool WasRequested(HybridPlayer h)=>false;}
 public static class ExistingItemEditHandler {PRODUCTION_MOVES_WITH}
 public static class FacingTargetHandler {public static void Clear(){}public static string Problem(HybridPlayer h,ItemObjectRoot i)=>null;public static bool LiveTarget(ItemObjectRoot i,ItemObjectRoot t)=>t!=null&&i!=t;}
 public static class ItemAlignmentHandler {public static void Clear(){}public static string Problem(HybridPlayer h,ItemObjectRoot i)=>null;}
 public static class RoomPlacementGrid {public static string Problem(ulong lot,int layer,Vector3 p)=>null;}
 // EDITING_STUBS_END
 public static class ModInputKey {public static bool Available=true;public static bool WasPressed(Keyboard k,Key key)=>k?.Pressed==key;public static bool IsAvailable(HybridPlayer p,Key key,out string conflict){conflict="hotkey_conflict";return Available;}}
 public static class ItemDescription {public static string GetProductIdentity(ItemObjectRoot r)=>r.Name;public static string GetBriefIdentity(ItemObjectRoot r)=>r.Name;public static string GetName(ItemObjectRoot r)=>r.Name;}
 internal static class PlacementGridReader {public static string Dimensions(ItemObjectRoot r)=>"dimensions";public static bool TryRead(ulong lot,float spacing,out PlacementGrid grid){grid=new PlacementGrid(0,0,0,spacing);return true;}public static string Position(PlacementGrid g,Vector3 p){g.Coordinates(p.x,p.z,out double x,out double y);return "column "+x+", row "+y;}}
 public static class WorldTargeting {public static bool Succeed=true;public static int Calls;public static bool FrameArea(Player p,ulong lot,int layer,Vector3 target,string reason){Calls++;return Succeed;}}
 public static class ItemPlacementHandler {
  public static bool Enabled=true,Visible=true;public static ulong Lot=1;public static float GridSpacingMetres=0.5f,CameraOffset;public static string Problem;
  public static ulong ResolvePlacementLot(Player p,out string source){source="camera";return Lot;}
  public static bool TryProjectTarget(HybridPlayer h,Vector3 world,out Vector2 screen){screen=new Vector2(500+world.x*19.37f+CameraOffset,300+world.z*19.37f+world.y*13.31f);return Visible;}
  private static int _blockerCheckedFrame;private static ItemObjectRoot _blockerCheckedRoot,_blockingObject;
  private static string GetPlacementProblem(ItemObjectRoot r)=>Problem;
  private static bool IsInsideOwnedLot(ItemObjectRoot r)=>r.LotPlacedOnGUID==Lot&&Lot!=0;
  PRODUCTION_GUARD
 }
 public static class SlotChecks {
  static int checks;static HybridPlayer H=>PlayerManager.Instance.HybridPlayer1;static ItemObjectInPlacement P=>H.Player.ItemInPlacement;
  static AssetLot Lot=>LotManager.Instance.Lot;static ItemObjectRoot Desk;static ItemObjectPlacementSlot Surface;static ItemObjectChairSlot Chair;
  static Type Handler=>typeof(SlotPlacementHandler);
  static object Get(string n)=>Handler.GetField(n,BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  static object Call(string n,params object[] args)=>Handler.GetMethod(n,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
  static bool Said(string key)=>ScreenReader.Spoken.Any(s=>s.Contains(key+"("));
  static void Check(bool ok,string name){if(!ok)throw new Exception(name+"; speech="+string.Join(" / ",ScreenReader.Spoken));checks++;}
  static bool Near(float a,float b)=>Math.Abs(a-b)<0.0001f;
  static List<ItemSlotTarget> Read()=>ItemSlotReader.Read(P.Item,1,0);
  static void Reset(){
   RoomBoundaryConstraint.Evaluate=null;RoomBoundaryConstraint.Enabled=false;
   SlotPlacementHandler.Clear();PlayerManager.Instance=new PlayerManager();LotManager.Instance=new LotManager();InputManager.Instance=new InputManager();ZoneManager.Instance=new ZoneManager();Keyboard.current=new Keyboard();Mouse.current=new Mouse();
   Settings.Values<Items>.Value=new Items();Settings.Values<BuildModeCatalogTags>.Value=new BuildModeCatalogTags();Settings.Values<SnappingAndAlignment>.Value=new SnappingAndAlignment();
   ItemPlacementHandler.Enabled=ItemPlacementHandler.Visible=SlotPlacementHandler.Enabled=ModInputKey.Available=WorldTargeting.Succeed=true;ItemPlacementHandler.Lot=1;ItemPlacementHandler.CameraOffset=0;ItemPlacementHandler.Problem=null;WorldTargeting.Calls=0;BuildModeRefreshManager.IsZoneStateRefreshed=true;
   Desk=new ItemObjectRoot{InstanceID=2,Name="Desk"};Lot.LotItems.Add(Desk);
   Surface=new ItemObjectPlacementSlot();Surface.Item.Root=Desk;Surface.transform.position=new Vector3(3,1,4);Surface.transform.rotation=new Quaternion{Yaw=0.8f};Desk.ItemPlacementSlots[Surface.GUID]=Surface;Lot.ItemPlacementSlots.Add(Surface);
   Chair=new ItemObjectChairSlot();Chair.Item.Root=Desk;Chair.transform.position=new Vector3(3,0,3);Chair.transform.rotation=new Quaternion{Yaw=0.8f};Desk.ItemChairSlots[Chair.GUID]=Chair;Lot.ItemChairSlots.Add(Chair);Surface.ChairSlotLinked=Chair;
   RaycastSystem.Objects=null;RaycastSystem.Surface.Clear();MoneyManager.Instance=new MoneyManager();Time.frameCount++;ScreenReader.Spoken.Clear();
  }
  static void Next(){Time.frameCount++;Keyboard.current=new Keyboard();}
  static void Open(){Keyboard.current.Pressed=Key.Semicolon;Check(SlotPlacementHandler.TryOpen(H,P),"semicolon opens picker");Next();}
  static void Before()=>SlotPlacementHandler.BeforeGameSystems(H,P);
  static void Enter(){Keyboard.current.enterKey.wasReleasedThisFrame=true;Before();}
  static void Select(bool chair=false){Open();if(chair){Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Before();Next();}Enter();Check(Get("_target")!=null&&H.ButtonConfirm.Releases==0,"slot targeting does not buy");Next();}
  static void Observe(bool matching=true){
   Call("BeforeNativeMove",H.Player);Mouse.current?.Arrive();P.CursorTeleported=false;
   if(matching){var t=(ItemSlotTarget)Get("_target");P.Item.CubeTransform.LocalPosition=t.Transform.position;P.Item.transform.rotation=t.Transform.rotation;
    if(P.Item.ItemOffset!=null)P.Item.CubeTransform.LocalPosition+=P.Item.transform.rotation*P.Item.ItemOffset.Offset;
    if(t.IsChair){P.Item.ChairSlotPlacedOn=t.Guid;Chair.ItemInSlotInstanceID=P.Item.InstanceID;P.Item.ItemAttachedTo=Desk;if(!Desk.ItemsAttachedToThis.Contains(P.Item))Desk.ItemsAttachedToThis.Add(P.Item);P.LastColliderType=ColliderType.None;P.LastCollidedObject=null;}
    else{P.Item.PlacementSlotPlacedOn=t.Guid;Surface.ItemOnInstanceID=P.Item.InstanceID;P.LastColliderType=ColliderType.Object;P.LastCollidedObject=Desk.gameObject;}
   }
   SlotPlacementHandler.AfterGameSystems(H,P);Next();
  }
  static void Settle(){for(int i=0;i<3;i++){Before();Observe();}}
  static void Rays(bool chair=false){RaycastSystem.Floor=(new RaycastHit{point=chair?Chair.transform.position:new Vector3(3,0,4),normal=Vector3.up,distance=10},ColliderType.Floor,new GameObject());RaycastSystem.Surface.Clear();if(!chair)RaycastSystem.Surface.Add((new RaycastHit{point=Surface.transform.position,normal=Vector3.up,distance=5},ColliderType.Object,Desk.gameObject));}
  static void Ready(bool chair=false){Select(chair);Settle();Rays(chair);Check(Said("slot_ready"),"matching slot, support and facing becomes ready");}
  static void End(){Call("BeforeNativeEnd",H.Player);Call("AfterNativeEnd",H.Player);}
  static void Refuse(Action change,string name,bool chair=false){Reset();Ready(chair);Enter();change();End();Check(H.ButtonConfirm.Releases==0,name);}
  public static string Run(){
   Check(SlotPlacementHandler.ApplyPatches(new Harmony()),"movement and scoped end hooks are verified");
   Reset();var targets=Read();Check(targets.Count==2&&!targets[0].IsChair&&targets[1].IsChair,"surface and chair targets use stable owner/type/GUID order");Check(Surface.TagCalls>0&&Settings.Get<BuildModeCatalogTags>().Calls>0,"compatibility calls native tag readers");
   Surface.ItemOnInstanceID=99;Check(Read().Count==2&&!targets[0].AvailableFor(P.Item),"occupied compatible slots stay discoverable but unavailable");Surface.ItemOnInstanceID=P.Item.InstanceID;Check(targets[0].AvailableFor(P.Item),"own preview reservation is allowed");
   Surface.PlacementSlotForAnimation=true;Check(Read().Count==1,"animation-only slot excluded");Surface.PlacementSlotForAnimation=false;Surface.Compatible=false;Check(Read().Count==1,"native surface compatibility refusal honored");Surface.Compatible=true;Chair.ChairTags=new List<int>{99};Check(Read().Count==1,"chair tags constrain targets");
   Reset();Settings.Get<Items>().Current.Tag=null;Check(Read().Count==0,"missing catalogue tags fail closed");
   Reset();P.IsNewItem=false;P.Item.ItemsOnThis.Add(Desk);Check(Read().Count==0,"existing support cannot target slots on its own carried descendant");
   Reset();P.IsNewItem=false;MoneyManager.Instance.Affordable=false;Ready();Enter();End();Check(H.ButtonConfirm.Releases==1,"existing item uses native surface slot without purchase funds");
   Reset();P.IsNewItem=false;MoneyManager.Instance.Affordable=false;Ready(true);Enter();End();Check(H.ButtonConfirm.Releases==1,"existing chair uses native chair slot without purchase funds");
   Reset();Desk.Layer=1;Check(Read().Count==0,"different floor layer excluded");Desk.Layer=0;Desk.LotPlacedOnGUID=2;Check(Read().Count==0,"different lot excluded");Desk.LotPlacedOnGUID=1;Desk.isActiveAndEnabled=false;Check(Read().Count==0,"inactive support excluded");
   Reset();Surface.isActiveAndEnabled=Chair.isActiveAndEnabled=false;Check(Read().Count==0,"inactive slots excluded");
   Reset();Lot.LotItems.Clear();Check(Read().Count==0,"unregistered preview supports excluded");
   Reset();Desk.ItemPlacementSlots.Clear();Desk.ItemChairSlots.Clear();Check(Read().Count==0,"both owner dictionaries must reference actual slots");
   Reset();Surface.GUID=0;Chair.GUID=0;Check(Read().Count==0,"zero GUIDs cannot establish a saved relationship");
   Reset();Surface.Item.Root=P.Item;Chair.Item.Root=P.Item;Check(Read().Count==0,"self slots excluded");
   Reset();Surface.transform.position=new Vector3(float.NaN,0,0);Chair.transform.rotation=new Quaternion{Yaw=float.NaN};Check(Read().Count==0,"nonfinite target geometry excluded");
   Reset();var original=Read()[0];Desk.InstanceID=72;Check(!original.IsLive(P.Item,1,0),"owner instance identity is captured");
   Reset();original=Read()[0];Desk.ItemPlacementSlots[Surface.GUID]=new ItemObjectPlacementSlot();Check(!original.IsLive(P.Item,1,0),"replacement component with same GUID invalidates target");
   Reset();Lot.ItemPlacementSlots.Add(Surface);Lot.ItemChairSlots.Add(Chair);Check(Read().Count==2,"duplicate list entries do not duplicate picker targets");
   foreach(var kind in new[]{0,1,2}){Reset();P.Item.IsWallItem=kind==0;P.Item.IsCeilingItem=kind==1;P.Item.IsLotMovementItem=kind==2;Keyboard.current.Pressed=Key.Semicolon;Check(Read().Count==0&&!SlotPlacementHandler.TryOpen(H,P),"unsupported preview types retain their workflow "+kind);}
   Reset();Check(Read()[0].DescribeLinks(1).Contains("slot_link_empty"),"authored empty chair link is reported");Surface.ChairSlotLinked=null;Check(Read()[0].DescribeLinks(1).Contains("slot_no_chair_link"),"absent link is distinct from empty link");
   Reset();Chair.ItemInSlotInstanceID=33;Check(Read()[0].DescribeLinks(1).Contains("slot_link_unverified"),"stale occupant ID is not a connected chair");
   var seat=new ItemObjectRoot{InstanceID=33,Name="Chair",ChairSlotPlacedOn=Chair.GUID,ItemAttachedTo=Desk};Lot.LotItems.Add(seat);Desk.ItemsAttachedToThis.Add(seat);Check(Read()[0].DescribeLinks(1).Contains("slot_link_chair(Chair)"),"chair report verifies reciprocal owner slot and attachment");seat.ItemAttachedTo=null;Check(Read()[0].DescribeLinks(1).Contains("slot_link_unverified"),"facing near desk without attachment does not prove chair link");
   Reset();Check(Read()[1].DescribeLinks(1).Contains("slot_link_surfaces_empty"),"chair lists empty linked surface slots");Surface.ItemOnInstanceID=44;var computer=new ItemObjectRoot{InstanceID=44,ItemPlacedOn=Desk,PlacementSlotPlacedOn=Surface.GUID};Lot.LotItems.Add(computer);Desk.ItemsOnThis.Add(computer);Check(Read()[1].DescribeLinks(1).Contains("slot_link_items(Computer)"),"reverse chair link names the placed surface item");computer.PlacementSlotPlacedOn=999;Check(Read()[1].DescribeLinks(1).Contains("slot_link_unverified"),"reverse link validates surface GUID");
   Reset();ModInputKey.Available=false;Keyboard.current.Pressed=Key.Semicolon;Check(SlotPlacementHandler.TryOpen(H,P)&&!SlotPlacementHandler.Active&&ScreenReader.Spoken.Contains("hotkey_conflict"),"rebound semicolon announces conflict without opening");
   Reset();Open();Check(Mouse.current.Warps==0&&H.ButtonConfirm.Releases==0,"browsing neither moves nor buys");Check(SlotPlacementHandler.Describe().Contains("column 6")&&SlotPlacementHandler.Describe().Contains("row 8"),"slot address uses lot grid frame");
   Keyboard.current.leftArrowKey.wasPressedThisFrame=true;Before();Check((int)Get("_index")==1,"list wraps backwards");Next();Keyboard.current.Pressed=Key.Semicolon;Before();Check(!SlotPlacementHandler.Active,"semicolon closes browse list");
   Reset();Select();Keyboard.current.Pressed=Key.Semicolon;Before();Check(SlotPlacementHandler.Active&&Get("_target")==null,"first semicolon leaves target for list");Next();Keyboard.current.Pressed=Key.Semicolon;Before();Check(!SlotPlacementHandler.Active,"second semicolon returns to grid");
   Reset();Open();Surface.ItemOnInstanceID=99;Enter();Check(Get("_target")==null&&Said("slot_occupied"),"occupied entry cannot be targeted");
   Reset();Open();Lot.ItemPlacementSlots.Clear();Enter();Check(Get("_target")==null&&Said("slot_changed"),"stale browse entry refreshes without purchase");
   Reset();Lot.ItemPlacementSlots.Clear();Lot.ItemChairSlots.Clear();Open();Enter();Check(Said("slot_none")&&H.ButtonConfirm.Releases==0,"empty picker can refresh without purchasing");
   Reset();Open();RoomBoundaryConstraint.Evaluate=(lot,layer,point)=>"Room edge";Enter();Check(Get("_target")==null&&Mouse.current.Warps==0&&H.ButtonConfirm.Releases==0&&ScreenReader.Spoken.Contains("Room edge"),"surface/chair targeting cannot warp outside the locked room");
   Reset();Ready();Enter();RoomBoundaryConstraint.Evaluate=(lot,layer,point)=>"Room changed";End();Check(H.ButtonConfirm.Releases==0&&ScreenReader.Spoken.Contains("Room changed"),"slot native commit rechecks changed room restriction");
   Reset();Select();Enter();End();Check(H.ButtonConfirm.Releases==0&&Said("placement_wait"),"unobserved preview never confirms");
   Reset();Ready();Enter();Check(H.ButtonConfirm.Releases==0,"queue does not release shared confirm early");Call("BeforeNativeEnd",H.Player);Check(H.ButtonConfirm.Up&&H.ButtonConfirm.Releases==1,"release exists only inside native EndMoveItem");Call("AfterNativeEnd",H.Player);Check(!H.ButtonConfirm.Up&&H.ButtonConfirm.Releases==1,"cleanup clears release without duplicate OnUp");
   SlotPlacementHandler.AfterGameSystems(H,P);Check(Said("slot_not_placed"),"native no-op produces explicit failure report");Next();End();Check(H.ButtonConfirm.Releases==1,"failed native completion is never retried");
   Reset();Ready(true);Enter();End();Check(H.ButtonConfirm.Releases==1&&!H.ButtonConfirm.Up,"chair confirms with native attachment and facing");
   Refuse(()=>Surface.ItemOnInstanceID=99,"occupancy rechecked at native end");
   Refuse(()=>P.Item.PlacementSlotPlacedOn=999,"different selected surface GUID refused");
   Refuse(()=>P.LastCollidedObject=new ItemObjectRoot().gameObject,"correct GUID with wrong surface owner refused");
   Refuse(()=>P.LastColliderType=ColliderType.Floor,"slot GUID without supporting object ray refused");
   Refuse(()=>P.Item.ChairSlotPlacedOn=11,"conflicting native slot types refused");
   Refuse(()=>P.Item.ItemAttachedTo=new ItemObjectRoot(),"wrong chair attachment refused",true);
   Refuse(()=>Desk.ItemsAttachedToThis.Clear(),"missing reciprocal chair attachment refused",true);
   Refuse(()=>P.Item.ChairSlotPlacedOn=777,"chair slot GUID mismatch refused",true);
   Refuse(()=>P.LastCollidedObject=Desk.gameObject,"chair collision state must match native snap",true);
   Refuse(()=>P.Item.LotPlacedOnGUID=2,"preview lot mismatch refused");
   Refuse(()=>P.Item.Layer=1,"preview layer mismatch refused");
   Refuse(()=>ItemPlacementHandler.Lot=2,"camera owned-lot change cancels pending purchase");
   Refuse(()=>H.Player.Layer=1,"selected floor change cancels pending purchase");
   Refuse(()=>Surface.transform.position+=new Vector3(0.1f,0,0),"moved support slot invalidates reviewed target");
   Refuse(()=>Surface.transform.rotation=new Quaternion{Yaw=2},"rotated slot invalidates target");
   Refuse(()=>Surface.Compatible=false,"compatibility change rechecked");
   Refuse(()=>P.Item.Price++,"price change invalidates observation");
   Refuse(()=>P.Item.CurrentSkinGUID=45,"skin change invalidates observation");
   Refuse(()=>P.Item.CubeTransform.FinalSize=new Vector3(2,2,2),"resize invalidates observation");
   Refuse(()=>P.Item.ItemOffset=new ItemOffset{Offset=new Vector3(0.2f,0,0)},"offset change invalidates observation");
   Refuse(()=>H.HasInputFieldFocused=true,"text field retains input");
   Refuse(()=>H.Player.PlayerActionBlockedByUIWindow=true,"modal retains input");
   Refuse(()=>H.IsUsingGamePad=true,"gamepad retains input");
   Refuse(()=>H.Player.PlayerActionsAreAllowed=false,"disabled player actions prevent purchase");
   Refuse(()=>H.Player.State=GameStates.LiveMode,"mode change prevents purchase");
   Refuse(()=>H.ButtonCancel.Down=true,"cancel wins over queued Enter");
   Refuse(()=>H.ButtonUndo.Down=true,"undo wins over queued Enter");
   Refuse(()=>H.ButtonRedo.Down=true,"redo wins over queued Enter");
   Refuse(()=>Keyboard.current.leftAltKey.isPressed=true,"Alt cannot disable snapping during confirmation");
   Refuse(()=>Keyboard.current.rightCtrlKey.isPressed=true,"Control combination not interpreted as placement");
   Refuse(()=>Keyboard.current.rightShiftKey.isPressed=true,"Shift combination not interpreted as placement");
   Refuse(()=>InputManager.Instance.OverUI=true,"UI pointer prevents purchase");
   Refuse(()=>MoneyManager.Instance.Affordable=false,"insufficient funds prevents purchase");
   Refuse(()=>ItemPlacementHandler.Problem="native invalid","native invalid placement prevents purchase");
   Refuse(()=>BuildModeRefreshManager.IsZoneStateRefreshed=false,"native room refresh gate observed");
   Refuse(()=>ZoneManager.Instance.DirtyWallZones.Add(1),"dirty room gate observed");
   Refuse(()=>H.Player.ItemInRotation=new object(),"native rotation widget owns input");
   Refuse(()=>P.IsInOffset=true,"native offset mode owns input");
   Refuse(()=>P.FromRotationWidget=true,"rotation preview is not normal slot placement");
   Refuse(()=>ItemPlacementHandler.CameraOffset=3,"changed projection at transaction boundary refused");
   Refuse(()=>RaycastSystem.Surface.Clear(),"vanished surface ray blocks confirmation");
   Refuse(()=>RaycastSystem.Floor=(default,ColliderType.None,null),"missing native floor ray blocks confirmation");
   Refuse(()=>RaycastSystem.Surface[0]=(new RaycastHit{point=new Vector3(80,1,4),normal=Vector3.up,distance=5},ColliderType.Object,Desk.gameObject),"slot proximity rechecked against actual ray point");
   Refuse(()=>Desk.ItemCanBeStackedOn=false,"native stack permission rechecked");
   Refuse(()=>RaycastSystem.Surface.Insert(0,(new RaycastHit{point=Surface.transform.position,normal=Vector3.up,distance=4},ColliderType.Object,new ItemObjectRoot().gameObject)),"nearer compatible surface cannot replace selected support");
   Reset();Ready();Enter();Next();End();Check(H.ButtonConfirm.Releases==0,"queued Enter expires with frame");
   Reset();Ready();Enter();Call("BeforeNativeEnd",new Player());Check(H.ButtonConfirm.Releases==0,"other players do not receive release");End();Check(H.ButtonConfirm.Releases==1,"other player did not consume this player's pending frame");
   Reset();Ready();Enter();H.ButtonConfirm.Down=true;End();Check(H.ButtonConfirm.Releases==0&&H.ButtonConfirm.Down,"physical mouse press is untouched");
   Reset();Ready();Enter();H.ButtonConfirm.Throw=true;End();Check(H.ButtonConfirm.Releases==1&&!H.ButtonConfirm.Up&&!SlotPlacementHandler.Enabled,"OnUp exception still cleans injected release and disables bridge");Keyboard.current.Pressed=Key.Semicolon;Before();Check(!SlotPlacementHandler.Active,"disabled bridge can be left for grid");
   Reset();Ready();Keyboard.current.rightArrowKey.wasPressedThisFrame=true;Keyboard.current.enterKey.wasReleasedThisFrame=true;Before();End();Check(Get("_target")==null&&H.ButtonConfirm.Releases==0,"browse and Enter together do not buy");
   Reset();Select();var oldCursor=InputManager.Instance.Cursor;Observe();InputManager.Instance.Cursor=oldCursor;Mouse.current.position.Value=oldCursor;Before();Check(Get("_target")!=null&&!Said("slot_manual"),"pending automatic warp does not discard the target before either input stream arrives");
   Reset();Ready();InputManager.Instance.Cursor=new Vector2(1,1);Before();Check(Get("_target")!=null&&!Said("slot_manual"),"stale legacy cursor does not discard a ready native slot when the device cursor has arrived");
   Mouse.current.position.Value=InputManager.Instance.Cursor;Before();Call("BeforeNativeMove",H.Player);Check(Get("_target")==null&&!P.CursorTeleported&&Said("slot_manual"),"manual cursor movement releases target after automatic arrival");
   Reset();Ready();H.ButtonConfirm.Pressed=true;Before();int takeoverReports=ScreenReader.Spoken.Count(s=>s.Contains("slot_manual("));Next();Before();Check(takeoverReports==1&&ScreenReader.Spoken.Count(s=>s.Contains("slot_manual("))==1,"held mouse announces takeover once and leaves native input intact");
   Reset();Ready();var exact=(Vector2)Get("_screen");InputManager.Instance.Cursor=new Vector2((float)Math.Round(exact.x),(float)Math.Round(exact.y));Before();Call("BeforeNativeMove",H.Player);Check(P.CursorTeleported&&Near(P.CursorTeleportPosition.x,exact.x),"fractional cursor position handed off each native move pass");
   Reset();Ready();H.HasInputFieldFocused=true;SlotPlacementHandler.AfterGameSystems(H,P);H.HasInputFieldFocused=false;Check(Get("_target")==null,"foreground loss requires explicit retargeting");
   foreach(var mode in new[]{0,1,2}){Reset();Ready();P.FromRotationWidget=mode==0;P.IsInOffset=mode==1;H.Player.ItemInRotation=mode==2?new object():null;int warps=Mouse.current.Warps;Call("BeforeNativeMove",H.Player);Before();Check(!P.CursorTeleported&&Mouse.current.Warps==warps,"rotation/offset tools receive no cursor teleport or physical warp "+mode);}
   Reset();Ready();Keyboard.current.Pressed=Key.Home;Before();Check(WorldTargeting.Calls==2&&(int)Get("_stable")==0,"Home reframes and invalidates prior observations");
   Reset();ItemPlacementHandler.Visible=false;Select();Check(Said("slot_out_of_view"),"offscreen target has explicit guidance");Enter();End();Check(H.ButtonConfirm.Releases==0,"offscreen target never buys");
   Reset();Mouse.current=null;Open();Enter();Check(Said("placement_mouse_missing"),"missing mouse reports inability to aim");
   Reset();P.Item.ItemOffset=new ItemOffset{Offset=new Vector3(0.2f,0.3f,0.4f)};P.LocalMouseOffset=new Vector3(0.1f,0.1f,0.1f);H.Player.HasSnap=true;var aim=SlotPlacementHandler.AimPoint(H.Player,P,Vector3.zero);Check(Near(aim.x,0.1f)&&Near(aim.y,0.2f)&&Near(aim.z,0.3f),"aim inverts native ray offset including mouse snap offset");Check(Near(P.Item.CubeTransform.LocalPosition.x,0)&&P.Item.PlacementSlotPlacedOn==0,"aim never changes transforms or relationships");
   Ready();Enter();End();Check(H.ButtonConfirm.Releases==1,"native item offset is accepted after stable observation");
   Reset();Ready();P.Item.CubeTransform.LocalPosition+=new Vector3(1,0,0);Observe(false);Before();Observe(false);Check(SlotPlacementHandler.Describe().Contains("slot_alignment_changed"),"stable but displaced preview is not an exact slot match");
   Reset();Ready();P.Item.transform.rotation=new Quaternion{Yaw=2};Observe(false);Before();Observe(false);Check(SlotPlacementHandler.Describe().Contains("slot_alignment_changed"),"stable wrong facing does not pass semantic match");
   Reset();Ready();RaycastSystem.Objects=new List<GameObject>{Desk.gameObject};Enter();End();Check(H.ButtonConfirm.Releases==1,"actual slot support is not an obstruction");
   Reset();Ready(true);RaycastSystem.Objects=new List<GameObject>{Desk.gameObject};Enter();End();Check(H.ButtonConfirm.Releases==1,"chair attachment support is not an obstruction");
   Reset();Ready(true);var linkedComputer=new ItemObjectRoot{InstanceID=9,Name="Computer",ItemPlacedOn=Desk,PlacementSlotPlacedOn=Surface.GUID};Desk.ItemsOnThis.Add(linkedComputer);Lot.LotItems.Add(linkedComputer);Surface.ItemOnInstanceID=linkedComputer.InstanceID;RaycastSystem.Objects=new List<GameObject>{linkedComputer.gameObject};
   Check(SlotPlacementHandler.AllowsLinkedOverlap(P.Item,linkedComputer),"reviewed chair recognizes its actual occupied linked surface");Enter();End();Check(H.ButtonConfirm.Releases==1,"native linked linkedComputer bounding box does not block reviewed chair purchase");
   Next();Surface.ItemOnInstanceID=99;Enter();End();Check(H.ButtonConfirm.Releases==1,"late linked occupant change restores overlap refusal");
   Surface.ItemOnInstanceID=linkedComputer.InstanceID;Desk.ItemsOnThis.Clear();Check(!SlotPlacementHandler.AllowsLinkedOverlap(P.Item,linkedComputer),"missing reciprocal surface relationship cannot exempt overlap");Desk.ItemsOnThis.Add(linkedComputer);
   Surface.ChairSlotLinked=null;Check(!SlotPlacementHandler.AllowsLinkedOverlap(P.Item,linkedComputer),"an unlinked surface on the same desk is still an obstacle");Surface.ChairSlotLinked=Chair;
   linkedComputer.Layer=1;Check(!SlotPlacementHandler.AllowsLinkedOverlap(P.Item,linkedComputer),"linked counterpart on a different floor cannot exempt overlap");linkedComputer.Layer=0;
   SlotPlacementHandler.Clear();Check(!SlotPlacementHandler.AllowsLinkedOverlap(P.Item,linkedComputer),"leaving the explicit slot review removes linked overlap exemption");
   Reset();Ready();var seated=new ItemObjectRoot{InstanceID=9,Name="Chair",ItemAttachedTo=Desk,ChairSlotPlacedOn=Chair.GUID};Desk.ItemsAttachedToThis.Add(seated);Lot.LotItems.Add(seated);Chair.ItemInSlotInstanceID=seated.InstanceID;RaycastSystem.Objects=new List<GameObject>{seated.gameObject};
   Check(SlotPlacementHandler.AllowsLinkedOverlap(P.Item,seated),"reviewed computer recognizes the actual occupied linked chair");Enter();End();Check(H.ButtonConfirm.Releases==1,"linked chair bounding box does not block reviewed computer purchase");
   Chair.ItemInSlotInstanceID=-1;Check(!SlotPlacementHandler.AllowsLinkedOverlap(P.Item,seated),"stale chair slot occupancy cannot exempt overlap");
   Refuse(()=>RaycastSystem.Objects=new List<GameObject>{new ItemObjectRoot{InstanceID=9,Name="Blocker"}.gameObject},"unrelated overlap blocks native slot confirmation");
   Reset();Ready();var other=new ItemObjectRoot{InstanceID=9};var duplicate=new ItemObjectPlacementSlot{GUID=Surface.GUID};duplicate.Item.Root=other;Lot.ItemPlacementSlots.Add(duplicate);RaycastSystem.Objects=new List<GameObject>{other.gameObject};Enter();End();Check(H.ButtonConfirm.Releases==0,"same slot GUID on another object does not exempt that obstruction");
   Reset();Ready();P.InventoryItem=new object();MoneyManager.Instance.Affordable=false;Enter();End();Check(H.ButtonConfirm.Releases==1,"native inventory preview does not need new-purchase funds");
   Reset();Ready();Check(SlotPlacementHandler.DescribeResult(P.Item).Contains("slot_result_unverified"),"preview slot reservation alone is not post-purchase surface attachment");P.Item.ItemPlacedOn=Desk;Desk.ItemsOnThis.Add(P.Item);Check(SlotPlacementHandler.DescribeResult(P.Item).Contains("slot_surface_placed"),"result validates reciprocal native surface attachment");
   Reset();Ready(true);Check(SlotPlacementHandler.DescribeResult(P.Item).Contains("slot_chair_placed"),"result validates native chair slot and reciprocal attachment");Desk.ItemsAttachedToThis.Clear();Check(SlotPlacementHandler.DescribeResult(P.Item).Contains("slot_result_unverified"),"missing result attachment is not reported as linked");
   Reset();Ready(true);P.Item.CubeTransform.LocalPosition=new Vector3(float.NaN,0,0);Check(SlotPlacementHandler.DescribeResult(P.Item).Contains("slot_result_unverified"),"nonfinite placed geometry cannot be reported as verified");
   Reset();Ready();SlotPlacementHandler.Clear();Call("BeforeNativeMove",H.Player);Enter();End();Check(!SlotPlacementHandler.Active&&H.ButtonConfirm.Releases==0&&!P.CursorTeleported,"cleared preview cannot leak movement or confirmation into next preview");
   return checks+" slot reader, link, controller and transaction-guard checks passed. Native snapping, purchase, undo, persistence and audible delivery remain runtime checks.";
  }
 }
}
