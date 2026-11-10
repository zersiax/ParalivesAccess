$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
# Reuse native boundary adapters. These checks validate approval lifetime, not Unity stacking.
$adapters = Get-Content -Raw (Join-Path $PSScriptRoot 'fixtures/SlotPlacementAdapters.cs')
$adapters = $adapters.Substring(0, $adapters.IndexOf(' public static class ItemPlacementHandler {')) + "`n}"
$adapters = $adapters.Replace('public static partial class PlacementSupportConsent {public static string FloorProblem(ItemObjectInPlacement p)=>null;public static string Problem(ItemObjectInPlacement p)=>null;}', '')
$adapters = $adapters.Replace('PRODUCTION_MOVES_WITH', '')
$adapters = $adapters.Replace('public bool IsWallItem,', 'public bool IsPositionIllegal,IsWallItem,')
$adapters = $adapters.Replace('public static string Dimensions', 'public static string Length(float n)=>n+" m";public static string Dimensions')
$adapters = $adapters.Replace('public static Vector3 up=>', 'public static Vector3 down=>new Vector3(0,-1,0);public static Vector3 up=>')
$adapters = $adapters.Replace('public class AssetLot {', 'public class AssetLot {public LotData Data=new LotData();')
$adapters = $adapters.Replace('public struct RaycastHit {', 'public struct RaycastHit {public object collider;')
$adapters = $adapters.Replace('public static class RaycastSystem {', 'public static partial class RaycastSystem {')
$sources = @('PlacementGrid','PlacementSupportConsent') | ForEach-Object {
    (Get-Content -Raw (Join-Path $projectRoot "src/Handlers/$_.cs")) -replace '(?m)^using [^;]+;\r?\n', ''
}
$checks = @'
public class ItemManager {public static ItemManager Instance=new ItemManager();public ItemObjectRoot Owner;public ItemObjectRoot GetItemByInstanceID(int id)=>Owner?.InstanceID==id?Owner:null;}
public class WallHeightManager {public static WallHeightManager Instance=new WallHeightManager();public float GetFloorWorldY(ulong lot,int layer)=>0;}
public class LotData {public int LayerOffset;}
public class ZoneWallObject {public ulong LotPlacedOnGUID=1;public int Layer;}
public class ZoneWallPlatformObject {public ZoneWallObject WallZone=new ZoneWallObject();public bool DisabledByUser,DisabledButVisible,IsCeiling;}
public class ZoneWallPlatformRenderer {public ZoneWallPlatformObject WallZonePlatform=new ZoneWallPlatformObject();}
public static partial class RaycastSystem {
 public static List<(Vector3 point,ColliderType colliderType,GameObject gameObject)> Heights=new List<(Vector3,ColliderType,GameObject)>();public static bool Hole;
 public static List<(Vector3 point,ColliderType colliderType,GameObject gameObject)> CastAllGetAll(Vector3 p,Vector3 d,float distance,bool includeTerrain,bool includeFloor,bool includeWalls,bool includeObjects,bool includeInvisibleFloors)=>Heights;
 public static (RaycastHit hit,ColliderType type,GameObject gameObject) Cast(Vector3 p,Vector3 d,float distance,bool includeTerrain,bool includeFloor,bool includeWalls,bool includeObjects)=>(new RaycastHit{collider=new object()},ColliderType.Terrain,new GameObject());
 public static bool CheckIfTerrainHole(RaycastHit hit)=>Hole;
}
namespace ParalivesAccess.Handlers {
 public static class WallFaceFrame {public static bool Finite(Vector3 p)=>PlacementGrid.Finite(p.x)&&PlacementGrid.Finite(p.y)&&PlacementGrid.Finite(p.z);}
 public static class ItemPlacementHandler {public static float GridSpacingMetres=.5f;}
 public static class SlotPlacementHandler {public static bool Active;}
 public static class SupportChecks {
  static int count;static void Check(bool ok,string detail){if(!ok)throw new Exception(detail);count++;}
  public static string Run(){
   var h=new HybridPlayer();var p=h.Player.ItemInPlacement;var owner=new ItemObjectRoot{InstanceID=7,Name="Toilet"};
   ItemManager.Instance.Owner=owner;p.LastColliderType=ColliderType.Object;p.LastCollidedObject=owner.gameObject;
   PlacementSupportConsent.Begin(p);Check(PlacementSupportConsent.Problem(p).Contains("placement_support_review"),"unselected surface requires approval");
   Check(PlacementSupportConsent.Describe(p).Contains("Toilet"),"support identity is included");
   Check(PlacementSupportConsent.Approve(h,p).Contains("placement_support_approved")&&PlacementSupportConsent.Problem(p)==null,"explicit current position approval");
   p.Item.CubeTransform.LocalPosition=new Vector3(.1f,0,0);Check(PlacementSupportConsent.Problem(p)!=null,"moving preview invalidates approval");
   PlacementSupportConsent.Approve(h,p);owner.CubeTransform.LocalPosition=new Vector3(0,0,.2f);Check(PlacementSupportConsent.Problem(p)!=null,"moving owner invalidates approval");
   PlacementSupportConsent.Approve(h,p);owner.transform.rotation=new Quaternion{Yaw=.5f};Check(PlacementSupportConsent.Problem(p)!=null,"rotating owner invalidates approval");
   PlacementSupportConsent.Approve(h,p);owner.CubeTransform.FinalSize=new Vector3(2,1,1);Check(PlacementSupportConsent.Problem(p)!=null,"resizing owner invalidates approval");
   p.Item.IsPositionIllegal=true;Check(PlacementSupportConsent.Approve(h,p).Contains("placement_support_changed"),"illegal preview cannot be approved");p.Item.IsPositionIllegal=false;
   SlotPlacementHandler.Active=true;Check(PlacementSupportConsent.Problem(p)==null,"explicit compatible-slot handler retains authority");SlotPlacementHandler.Active=false;
   p.IsNewItem=false;p.Item.ItemPlacedOn=owner;PlacementSupportConsent.Begin(p);Check(PlacementSupportConsent.Problem(p)==null,"existing attached object starts with its current surface");
   owner.Layer=1;Check(PlacementSupportConsent.Current(p)==null,"support on different floor is not offered");owner.Layer=0;
   ItemManager.Instance.Owner=null;Check(PlacementSupportConsent.Current(p)==null,"removed owner is not offered");
   var floor=new ZoneWallPlatformRenderer();RaycastSystem.Heights.Add((new Vector3(0,0,0),ColliderType.Floor,new GameObject{Component=floor}));
   p.Item.CubeTransform.LocalPosition=new Vector3(0,-2.76f,0);p.Item.ItemOffset=new ItemOffset{Offset=new Vector3(0,-2.76f,0)};
   Check(PlacementSupportConsent.FloorProblem(p)?.Contains("placement_below_floor")==true,"buried native offset is refused against actual floor height");
   p.Item.CubeTransform.LocalPosition=new Vector3(0,.001f,0);Check(PlacementSupportConsent.FloorProblem(p)==null,"valid floor preview allowed");
   p.Item.CubeTransform.LocalPosition=new Vector3(0,-.02f,0);Check(PlacementSupportConsent.FloorProblem(p)==null,"small native support tolerance allowed");
   p.Item.CubeTransform.LocalPosition=new Vector3(0,-1,0);floor.WallZonePlatform.WallZone.Layer=1;Check(PlacementSupportConsent.FloorProblem(p)==null,"other floor ignored");
   floor.WallZonePlatform.WallZone.Layer=0;floor.WallZonePlatform.WallZone.LotPlacedOnGUID=2;Check(PlacementSupportConsent.FloorProblem(p)==null,"other lot ignored");floor.WallZonePlatform.WallZone.LotPlacedOnGUID=1;
   floor.WallZonePlatform.DisabledByUser=true;Check(PlacementSupportConsent.FloorProblem(p)==null,"disabled floor ignored");floor.WallZonePlatform.DisabledByUser=false;
   floor.WallZonePlatform.IsCeiling=true;Check(PlacementSupportConsent.FloorProblem(p)==null,"ceiling supports upper floor only");p.Item.Layer=1;Check(PlacementSupportConsent.FloorProblem(p)!=null,"upper floor ceiling support checked");p.Item.Layer=0;
   RaycastSystem.Heights.Clear();RaycastSystem.Heights.Add((Vector3.zero,ColliderType.Terrain,new GameObject()));Check(PlacementSupportConsent.FloorProblem(p)!=null,"below ground refused");
   RaycastSystem.Hole=true;Check(PlacementSupportConsent.FloorProblem(p)==null,"terrain hole allows lower floor");RaycastSystem.Hole=false;
   p.Item.Layer=1;Check(PlacementSupportConsent.FloorProblem(p)==null,"ground ignored on another floor");p.Item.Layer=0;
   p.Item.IsWallItem=true;Check(PlacementSupportConsent.FloorProblem(p)==null,"wall workflow retains its own height validation");p.Item.IsWallItem=false;
   p.Item.CubeTransform.LocalPosition=new Vector3(float.NaN,0,0);Check(PlacementSupportConsent.FloorProblem(p)!=null,"nonfinite preview refused");
   return count+" surface approval lifetime checks passed; actual native stacking requires runtime evidence.";
  }
 }
}
'@
Add-Type -TypeDefinition ($adapters + "`n" + ($sources -join "`n") + "`n" + $checks) -Language CSharp
[ParalivesAccess.Handlers.SupportChecks]::Run()
