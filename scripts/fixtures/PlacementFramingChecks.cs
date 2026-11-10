namespace UnityEngine {
 public struct Rect {public float xMin,yMin,width,height;public float xMax=>xMin+width;public float yMax=>yMin+height;}
 public class Camera {
  public Rect pixelRect=new Rect{width=1920,height=1080};
  public Vector3 Projection=new Vector3(960,492.98f,24.48f);
  public Vector3 WorldToScreenPoint(Vector3 p)=>Projection;
 }
 public class Collider {}
 public struct RaycastHit {public Collider collider;public Vector3 point;}
}
public enum ColliderType {None,Terrain,Floor,Wall}
public static class RaycastSystem {
 public static Func<Vector3,Vector3> Hit;
 public static bool HasHit=true;
 public static ColliderType Type=ColliderType.Floor;
 public static List<Vector3> Screens=new List<Vector3>();
 public static (RaycastHit,ColliderType,GameObject) ScreenCastAndGoThroughTerrain(Vector3 screen,bool includeTerrain,bool includeFloor,bool includeWalls,bool includeObjects,bool includeWidgets){
  if(!includeTerrain||!includeFloor||includeWalls||includeObjects||includeWidgets)throw new Exception("framing ray must retain native floor/terrain policy");
  Screens.Add(screen);return (new RaycastHit{collider=HasHit?new Collider():null,point=Hit(screen)},Type,null);
 }
}
public static class PlaneCastService {
 public static Vector3 Result;
 public static Vector3 GetXZPlanePosition(int player,Vector3 screen,ulong lot)=>Result;
}
namespace ParalivesAccess.Handlers {
 public static partial class LotDescriptionChecks {
  private static LotPerimeterZoneObject _expectedLotPerimeter;
  private static List<Vector3> _expectedLotPoints;
  private static ZoneWallObject _anchorRoom;
  private static Vector3 _anchorWorldPosition;
  FRAMING_CANDIDATES
  FRAMING_METHODS
  public static string RunFraming(){
   int before=checks;Reset(74.8,74.8);
   _expectedLotPerimeter=ZoneManager.Instance.Perimeter;_expectedLotPoints=_expectedLotPerimeter.GetPoints();
   _anchorRoom=Room(36,36,40,40);_anchorWorldPosition=P(38,38);var camera=new Camera();
   RaycastSystem.HasHit=true;RaycastSystem.Type=ColliderType.Floor;RaycastSystem.Screens.Clear();
   RaycastSystem.Hit=s=>Math.Abs(s.y-492.98f)<0.1f?P(38,38):P(38.64,39.76);
   Check(TryFindInitialOwnedLotPoint(H,camera,out var screen,out var point,out var type,out int candidate)
    &&candidate==0&&Same(point,P(38,38))&&Math.Abs(screen.y-492.98f)<0.1f&&RaycastSystem.Screens.Count==1,
    "reported room starts at its projected anchor, not the higher viewport-centre ray");
   camera.Projection=new Vector3(-1,493,24);RaycastSystem.Screens.Clear();
   Check(TryFindInitialOwnedLotPoint(H,camera,out screen,out point,out type,out candidate)&&candidate==1&&screen.y==540,
    "off-screen anchor retains tested viewport fallback instead of clamping a ray");
   RaycastSystem.Hit=s=>P(38,46.53);
   Check(!TryFindInitialOwnedLotPoint(H,camera,out _,out _,out _,out _),"fallback cannot start furniture outdoors when the chosen anchor is a room");
   _anchorRoom=null;Check(TryFindInitialOwnedLotPoint(H,camera,out _,out _,out _,out _),"deliberately outdoor anchor can start outdoors inside the lot");
   RaycastSystem.Hit=s=>P(80,38);Check(!TryFindInitialOwnedLotPoint(H,camera,out _,out _,out _,out _),"no candidate may start beyond the actual lot boundary");
   RaycastSystem.HasHit=false;PlaneCastService.Result=P(38,38);_anchorRoom=ZoneManager.Instance.Zones[0] as ZoneWallObject;
   Check(TryFindInitialOwnedLotPoint(H,camera,out _,out point,out type,out _)&&type==ColliderType.None&&Same(point,P(38,38)),"native plane fallback still obeys room and lot membership");
   PlaneCastService.Result=new Vector3(float.NaN,0,0);Check(!TryFindInitialOwnedLotPoint(H,camera,out _,out _,out _,out _),"invalid fallback geometry refuses framing");
   return (checks-before)+" production placement-framing checks passed; projection and ray observations are adapters, Unity snapping remains a gameplay check.";
  }
 }
}
