using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
using ParalivesAccess.Speech;

namespace UnityEngine {
 public struct Vector2 { public float x,y; public Vector2(float x,float y){this.x=x;this.y=y;} }
 public struct Vector3 {
  public float x,y,z; public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
  public static Vector3 zero=>default; public static Vector3 up=>new Vector3(0,1,0);
  public static Vector3 operator +(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
  public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
  public static Vector3 operator *(Vector3 a,float b)=>new Vector3(a.x*b,a.y*b,a.z*b);
  public static Vector3 operator /(Vector3 a,float b)=>a*(1/b);
  public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;
  public float magnitude=>(float)Math.Sqrt(Dot(this,this)); public Vector3 normalized=>magnitude>0?this/magnitude:zero;
  public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;
  public static Vector3 Cross(Vector3 a,Vector3 b)=>new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
 }
 public struct Quaternion {public float Angle; public static Vector3 operator *(Quaternion q,Vector3 p)=>new Vector3((float)(p.x*Math.Cos(q.Angle)-p.z*Math.Sin(q.Angle)),p.y,(float)(p.x*Math.Sin(q.Angle)+p.z*Math.Cos(q.Angle)));}
 public class Transform {public Vector3 position,eulerAngles;public Quaternion rotation;public Vector3 TransformPoint(Vector3 p)=>position+rotation*p;public Vector3 TransformDirection(Vector3 p)=>rotation*p;}
 public class GameObject {public bool activeInHierarchy=true;}
 public class Mesh {public Vector3[] vertices,normals;}
}
namespace UnityEngine.InputSystem {
 public enum Key {None,J,N,F11}
 public class KeyState {public bool isPressed;}
 public class Keyboard {public static Keyboard current=new Keyboard();public Key Pressed;public KeyState leftCtrlKey=new KeyState(),rightCtrlKey=new KeyState(),leftAltKey=new KeyState(),rightAltKey=new KeyState(),leftMetaKey=new KeyState(),rightMetaKey=new KeyState(),leftShiftKey=new KeyState(),rightShiftKey=new KeyState();}
}
public enum State {Game,Loading}
public enum GameStates {LiveMode,BuildMode,PlaceSegment,Painting,CharacterCreator}
public static class GameLoadingManager {public static State State=State.Game;}
public class Player {public GameStates State=GameStates.BuildMode;public int Layer,PlayerIndex;public bool PlayerActionBlockedByUIWindow;public ItemObjectInPlacement ItemInPlacement;public object SegmentInPlacement;public int GetLotLayer(ulong lot)=>Layer;}
public class ItemObjectInPlacement {public ItemObjectRoot Item;}
public class HybridCameraFree {public Transform LookTarget=new Transform();}
public class HybridCamera {public HybridCameraFree FreeCamera=new HybridCameraFree();}
public class HybridPlayer {public Player Player=new Player();public HybridCamera HybridCamera=new HybridCamera();public bool HasInputFieldFocused;}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public class AssetLotGridData {public Vector2 OffsetFromOrigin;public float Rotation,CellDimension=1;}
public class AssetLot {public AssetLotGridData Grid=new AssetLotGridData();public AssetLotGridData GetGridData()=>Grid;}
public class LotManager {public static LotManager Instance=new LotManager();public AssetLot Lot=new AssetLot();public AssetLot GetLotByGUID(ulong id)=>id==1?Lot:null;}
public class NodeObject {public Vector3 Position;}
public class SegmentObject {public NodeObject NodeA,NodeB;public List<Vector3> SegmentPoints=new List<Vector3>();public bool IsCurved;public GameObject gameObject=new GameObject();public int InstanceID,Layer,PlacementPlayer=-1;public ulong LotPlacedOnGUID=1;public SegmentObject ReplacingSegment;public int Cluster;}
public class SegmentWallObject:SegmentObject {public List<SegmentWallSectionObject> WallSections=new List<SegmentWallSectionObject>();}
public class SegmentWallSectionObject {public int InstanceID,StartHeight;public GameObject gameObject=new GameObject();public SegmentWallObject Wall;public bool IsFence,DisabledByUser,DisabledButVisible;public SegmentWallSectionRenderer SideA,SideB;}
public enum WallVerticalSectionPartSides {SideA,SideB}
public class SegmentWallSectionRenderer {public GameObject gameObject=new GameObject();public Transform transform=new Transform();public Mesh Mesh;public SegmentWallSectionObject WallSection;public WallVerticalSectionPartSides WallSide;}
public class ZoneSegment {public SegmentObject Segment;public bool FromAToB=true;}
public class ZoneObject {public bool IsGlobal;public int InstanceID,Layer;public ulong LotPlacedOnGUID=1;public GameObject gameObject=new GameObject();public Vector3 AveragePosition;public List<ZoneSegment> Segments=new List<ZoneSegment>();public bool? ContainsOverride;}
public class ZoneWallObject:ZoneObject {}
public class LotPerimeterZoneObject:ZoneObject {public List<Vector3> GetPoints()=>Segments.Select(e=>e.FromAToB?e.Segment.SegmentPoints.First():e.Segment.SegmentPoints.Last()).ToList();}
public class ZoneWallPlatformObject {public ZoneWallObject WallZone;}
public class ZoneManager {public static ZoneManager Instance=new ZoneManager();public LotPerimeterZoneObject Perimeter;public List<ZoneObject> Zones=new List<ZoneObject>();public List<int> DirtyWallZones=new List<int>();public LotPerimeterZoneObject GetLotPerimeterZoneObjectByGUID(ulong id)=>id==1?Perimeter:null;public List<ZoneObject> GetLotZones(ulong id)=>Zones.Where(z=>z.LotPlacedOnGUID==id).ToList();}
public class SegmentManager {public static SegmentManager Instance=new SegmentManager();public List<SegmentObject> Walls=new List<SegmentObject>();public List<SegmentObject> GetLotSegments(ulong lot)=>Walls.Where(w=>w.LotPlacedOnGUID==lot).ToList();}
public static class BuildModeRefreshManager {public static bool IsZoneStateRefreshed=true;}
public static class MathService {
 // Reproduce 0.1.9.20637's finite-origin ray, including double counting shared corners.
 // A correct replacement here previously concealed the actual game's containment defect.
 public static bool ZoneObjectContainsPoint(ZoneObject zone,Vector3 p){
  int intersections=0;
  foreach(var e in zone.Segments)for(int i=1;i<e.Segment.SegmentPoints.Count;i++){
   var a=e.Segment.SegmentPoints[i-1];var b=e.Segment.SegmentPoints[i];
   if(Intersect(a,b,Vector3.zero,p))intersections++;
  }
  return intersections%2==1;
 }
 static float Det(Vector3 a,Vector3 b)=>a.x*b.z-a.z*b.x;
 static bool SameXZ(Vector3 a,Vector3 b)=>a.x==b.x&&a.z==b.z;
 static bool Intersect(Vector3 a,Vector3 b,Vector3 c,Vector3 d){
  if(SameXZ(b,d)||SameXZ(a,c)||SameXZ(b,c)||SameXZ(a,d))return true;
  float denominator=Det(b-a,c-d),t=Det(c-a,c-d)/denominator,u=Det(b-a,c-a)/denominator;
  return t>=0&&u>=0&&t<=1&&u<=1;
 }
}
public class ItemCubeTransform {public Vector3 LocalPosition,FinalSize=new Vector3(1,1,1);}
public struct BoundingBox {public Vector3 Center;}
public class ItemObjectRoot {
 public int InstanceID,Layer;public ulong LotPlacedOnGUID=1;public bool isActiveAndEnabled=true,CenterToWallThickness,IsWallItem,IsCeilingItem,IsLotMovementItem;
 public string Name;public ItemCubeTransform CubeTransform=new ItemCubeTransform();public Transform transform=new Transform();
 public ZoneWallPlatformObject FloorPlacedOn;public SegmentWallSectionObject WallSectionPlacedOn;public WallVerticalSectionPartSides WallSideOn;
 public ItemObjectRoot ItemPlacedOn,ItemAttachedTo;public BoundingBox GetWorldBoundingBox()=>new BoundingBox{Center=transform.position};
}
public class ItemManager {public static ItemManager Instance=new ItemManager();public List<ItemObjectRoot> Items=new List<ItemObjectRoot>();public List<ItemObjectRoot> GetLotItems(ulong lot)=>Items.Where(i=>i.LotPlacedOnGUID==lot).ToList();}
public static class MeasurementUnitService {public static float Multiplier=1;public static string Unit="m";public static string FormatLength(float metres,string format="0.0")=>(metres*Multiplier).ToString(format,CultureInfo.InvariantCulture)+" "+Unit;}
namespace ParalivesAccess {
 public static class Loc {
  private static readonly Dictionary<string,string> Text=new Dictionary<string,string>();
  static Loc(){ LOCALIZED_STRINGS }
  public static string Get(string key,params object[] args)=>string.Format(CultureInfo.InvariantCulture,Text[key],args);
 }
 public class Logger {public bool ExpectedError;public List<string> Lines=new List<string>();public void LogInfo(string s){Lines.Add(s);}public void LogError(string s){Lines.Add(s);if(!ExpectedError)throw new Exception(s);}}
 public static class Plugin {public static Logger Log=new Logger();}
}
namespace ParalivesAccess.Speech {public static class ScreenReader {public static List<string> Said=new List<string>();public static void Speak(string s){Said.Add(s);}public static string NormalizeForOutput(string s)=>s;}}
namespace ParalivesAccess.Handlers {
 public static class ModInputKey {public static bool Available=true;public static bool WasPressed(Keyboard k,Key key)=>key!=Key.None&&k.Pressed==key;public static bool IsAvailable(HybridPlayer h,Key key,out string conflict){conflict="native binding conflict";return Available;}}
 public static class ItemDescription {public static string GetProductIdentity(ItemObjectRoot item)=>item.Name;}
 public static class ItemPlacementHandler {public static float GridSpacingMetres=0.5f;public static ulong Lot=1;public static ulong ResolvePlacementLot(Player p,out string source){source="build";return Lot;}}
 public static class LotObjectHandler {public static ulong Lot=1;public static ulong ResolveCurrentLot(Player p,out string source){source="camera";return Lot;}}
 public static class LotBuildInputOwnership {public static bool OwnsInput;}
 public static class SettingsHandler {public static bool IsCapturingBindingInput;}
 public static class CoordinateEntryHandler {public static bool BlocksPlacement;}
 public static class TownMapLotHandler {public static bool Enabled=true,OwnsInput;}
 public static class BuildCatalogHandler {public static bool OwnsInput;}
 public static class TutorialHandler {public static bool OwnsInput;}
 public static class ContextActionHandler {public static bool OwnsInput;}
 public static class OccupationHandler {public static bool OwnsInput;}
}
