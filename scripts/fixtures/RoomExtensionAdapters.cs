namespace Setting {
 public class Gameplay {public bool UsesWallGrid=true;}
 public enum BuildModeTagModes {WallPlacement}
 public class UlongAndGuid {public ulong GUID;}
 public class BuildModeCatalogTag {public ulong GUID;public bool ChangesState=true;public GameStates SetGameState=GameStates.PlaceSegment;public SegmentTypes SetSegmentType=SegmentTypes.Wall;public WallHeightType SetWallHeightType=WallHeightType.FullWall;public UlongAndGuid[] ChildTags;}
 public partial class BuildModeCatalogTags {public ulong WallModeTag=1;public List<BuildModeCatalogTag> Tags=new List<BuildModeCatalogTag>{new BuildModeCatalogTag{GUID=1}};public BuildModeCatalogTag GetTagByGUID(ulong id)=>Tags.FirstOrDefault(t=>t.GUID==id);}
}
public enum SegmentTypes {Null,Wall}
public enum WallHeightType {Null,FullWall,HalfWall}
public enum WallPlacementMode {Straight,Curved}
public partial class Player {public SegmentTypes CurrentSegmentType;public WallHeightType CurrentWallHeightType;public WallPlacementMode WallPlacementMode;}
public class MeasurementUnit {public float MetricMultiplier=1;public string ShortName="m";}
public static partial class MeasurementUnitService {public static MeasurementUnit Unit=new MeasurementUnit();public static MeasurementUnit GetCurrentUnit()=>Unit;}
public class ParaToggle {public bool IsOn;}
public class UIBuildModeCatalogModes {public BuildModeTagModes TagMode;public ParaToggle[] Buttons=new[]{new ParaToggle(),new ParaToggle()};public bool Refuse;public void OnClick(int index){if(!Refuse)PlayerManager.Instance.HybridPlayer1.Player.WallPlacementMode=(WallPlacementMode)index;}}
public class UIBuildModeCatalog {public int Calls;public UIBuildModeCatalogModes[] Modes=new[]{new UIBuildModeCatalogModes()};public void SetModeFromShortcut(GameStates state,SegmentTypes type,ulong tag){Calls++;var p=PlayerManager.Instance.HybridPlayer1.Player;p.State=state;p.CurrentSegmentType=type;}public void ChangePlayerState(ulong guid){var tag=Settings.Get<BuildModeCatalogTags>().GetTagByGUID(guid);var p=PlayerManager.Instance.HybridPlayer1.Player;p.State=tag.SetGameState;p.CurrentSegmentType=tag.SetSegmentType;p.CurrentWallHeightType=tag.SetWallHeightType;}}
public static partial class UI {public static UIBuildModeCatalog Catalog=new UIBuildModeCatalog();public static T GetOrNull<T>(int index)where T:class=>Catalog as T;}
public class LotPerimeterZoneObject {public List<Vector3> Points=new List<Vector3>{new Vector3(-10,0,-10),new Vector3(10,0,-10),new Vector3(10,0,10),new Vector3(-10,0,10)};public List<Vector3> GetPoints()=>Points;}
public partial class ZoneManager {public LotPerimeterZoneObject Perimeter=new LotPerimeterZoneObject();public Func<Vector3,bool> ContainsLot=p=>Math.Abs(p.x)<10&&Math.Abs(p.z)<10;public LotPerimeterZoneObject GetLotPerimeterZoneObjectByGUID(ulong lot)=>Perimeter;public (bool,ulong) GetNodeCursorLotPerimeterOfHousehold(Vector3 p,ulong lot)=>(ContainsLot(p),lot);}
namespace ParalivesAccess.Handlers {
 internal static class WallConstructionHandler {internal static RoomExtensionReview Started;internal static string StartExtension(HybridPlayer h,RoomExtensionReview r){Started=r;return r.Description;}}
 public static class RoomExtensionChecks {
  static int checks;static HybridPlayer H=>PlayerManager.Instance.HybridPlayer1;
  static void Check(bool ok,string label){if(!ok)throw new Exception(label+"; speech="+string.Join(" | ",ScreenReader.Spoken));checks++;}
  static ZoneWallObject Setup(){
   BuildEditingChecks.Reset();Settings.Get<Gameplay>().UsesWallGrid=true;InputManager.Shift=InputManager.ControlOrCommand=InputManager.Alt=false;WidgetManager.Instance.Preview=null;BuildModeRefreshManager.IsZoneStateRefreshed=true;UI.Catalog=new UIBuildModeCatalog();MeasurementUnitService.Unit=new MeasurementUnit();Settings.Values<BuildModeCatalogTags>.Value=new BuildModeCatalogTags();WallConstructionHandler.Started=null;
   var room=BuildEditingChecks.Room(out var front,out var back);room.TestContains=p=>p.x>0&&p.x<6&&p.z>0&&p.z<4;
   var nodes=new Dictionary<string,NodeObject>();foreach(var edge in room.Segments){var w=edge.Segment;var a=w.SegmentPoints[0];var b=w.SegmentPoints[1];string ak=a.x+":"+a.z,bk=b.x+":"+b.z;if(!nodes.ContainsKey(ak))nodes[ak]=new NodeObject{Position=a};if(!nodes.ContainsKey(bk))nodes[bk]=new NodeObject{Position=b};w.NodeA=nodes[ak];w.NodeB=nodes[bk];}
   H.Player.ItemInPlacement=null;LotManager.Instance.Lot.LotItems.Clear();return room;
  }
  static RoomExtensionTarget Front(out ZoneWallObject room){room=Setup();return RoomExtensionReader.Read(room)[0];}
  static string Prepare(RoomExtensionTarget t,double depth,out RoomExtensionReview r)=>t.Prepare(depth,out r);
  static SegmentWallObject Obstacle(float ax,float az,float bx,float bz){var w=new SegmentWallObject{InstanceID=99,NodeA=new NodeObject{Position=new Vector3(ax,0,az)},NodeB=new NodeObject{Position=new Vector3(bx,0,bz)}};w.SegmentPoints.Add(w.NodeA.Position);w.SegmentPoints.Add(w.NodeB.Position);SegmentManager.Instance.Walls.Add(w);return w;}
  public static string Run(){
   var t=Front(out var room);Check(RoomExtensionReader.Read(room).Count==4,"four native room sides offered");Check(t.Name().Contains("extension_outside"),"outside side named without coordinates");
   var sideKeys=new[]{"room_front_wall","room_back_wall","room_left_wall","room_right_wall"};
   for(int i=0;i<4;i++)Check(RoomExtensionReader.Read(room)[i].Name().Contains(sideKeys[i]),"extension picker names the room side without reference setup "+i);
   RoomSpatialReader.SetReference(room,RoomSpatialReader.Faces(room)[2]);
   Check(t.Name().Contains("room_front_wall"),"advanced front-wall reference does not rotate extension directions away from ordinary lot controls");
   Check(Prepare(t,3,out var review)==null&&review.Width==6&&review.Depth==3,"extension shares full wall width and requested depth");Check(review.Plan.MinRow==-3&&review.Plan.MaxRow==0,"new room is outside native inward face");Check(review.Shared.Walls.Count==1&&review.Shared.Walls[0]==t.Walls[0],"exact existing side reused");
   Check(review.Description.Contains("room_front_wall")&&review.Description.Contains("extension_wall_direction(1"),"depth review repeats the selected direction and wall identifier");
   Check(Prepare(t,2.6,out review)==null&&review.Depth==3,"depth rounds to supported construction size");Check(Prepare(t,0,out review).Contains("extension_depth_invalid")&&review==null,"zero depth rejected");Check(Prepare(t,double.NaN,out review)!=null,"nonfinite depth rejected");
   foreach(int side in new[]{0,1,2,3}){room=Setup();t=RoomExtensionReader.Read(room)[side];Check(Prepare(t,2,out review)==null,"extension points away from room for side "+side);Check(review.Plan.Width*review.Plan.Depth==t.Width*2,"width and depth retain meaning for vertical side "+side);}
   t=Front(out room);Check(Prepare(t,20,out review).Contains("extension_lot_blocked"),"outside lot refused before wall tool");
   ZoneManager.Instance.Perimeter.Points=new List<Vector3>{new Vector3(-10,0,-10),new Vector3(2,0,-10),new Vector3(2,0,-1),new Vector3(3,0,-1),new Vector3(3,0,-10),new Vector3(10,0,-10),new Vector3(10,0,10),new Vector3(-10,0,10)};
   Check(Prepare(t,3,out review).Contains("extension_lot_blocked"),"concave lot notch rejected although all four corners pass native containment adapter");
   t=Front(out room);Obstacle(-2,-1,9,-1);Check(Prepare(t,3,out review).Contains("extension_wall_blocked"),"wall crossing entire room rejected with endpoints outside");
   t=Front(out room);Obstacle(2,-2,3,-1);Check(Prepare(t,3,out review).Contains("extension_wall_blocked"),"wall wholly inside proposed room rejected");
   t=Front(out room);var neighbor=new ZoneWallObject{InstanceID=98,TestContains=p=>p.z<0};neighbor.Segments.AddRange(room.Segments.Take(2));ZoneManager.Instance.Zones.Add(neighbor);Check(t.Name().Contains("extension_other_room")&&Prepare(t,3,out review).Contains("extension_room_blocked"),"existing room across shared wall named and refused");
   t=Front(out room);neighbor=new ZoneWallObject{InstanceID=98,TestContains=p=>p.z<0};neighbor.Segments.Add(new ZoneSegment{Segment=new SegmentWallObject()});neighbor.Segments.Add(new ZoneSegment{Segment=new SegmentWallObject()});ZoneManager.Instance.Zones.Add(neighbor);Check(Prepare(t,3,out review).Contains("extension_room_blocked"),"extension enclosed in a larger room rejected without boundary crossing");
   t=Front(out room);var item=new ItemObjectRoot{InstanceID=22};item.transform.position=item.CubeTransform.LocalPosition=new Vector3(3,0,-3);LotManager.Instance.Lot.LotItems.Add(item);Check(Prepare(t,3,out review).Contains("extension_item_blocked"),"new wall crossing furniture bounds refused");item.transform.position=item.CubeTransform.LocalPosition=new Vector3(3,0,-1.5f);Check(Prepare(t,3,out review)==null,"furniture entirely inside new room allowed");
   review.Plan.StartAt(review.Shared.Edge);Check(review.Problem()==null,"shared-edge rotation preserves space validation");
   t=Front(out room);t.Walls[0].InstanceID++;Check(Prepare(t,3,out review).Contains("extension_changed"),"reused wall identity rejected");
   t=Front(out room);t.Walls[0].WallSections[0].DisabledByUser=true;Check(Prepare(t,3,out review)!=null,"hidden shared wall cannot be used");
   t=Front(out room);Settings.Get<Gameplay>().UsesWallGrid=false;Check(Prepare(t,3,out review).Contains("wall_grid_disabled"),"native grid preference respected");
   t=Front(out room);Prepare(t,3,out review);Obstacle(-1,-1,8,-1);Check(review.Problem().Contains("extension_wall_blocked"),"space is rechecked after initial review");
   t=Front(out room);Prepare(t,3,out review);BuildModeRefreshManager.IsZoneStateRefreshed=false;Check(review.Problem().Contains("extension_updating"),"dirty native geometry defers confirmation");BuildModeRefreshManager.IsZoneStateRefreshed=true;
   t=Front(out room);Check(RoomExtensionReader.SelectWallTool(H)&&UI.Catalog.Calls==1&&H.Player.CurrentWallHeightType==WallHeightType.FullWall,"native catalogue sets full straight wall tool");
   t=Front(out room);H.Player.WallPlacementMode=WallPlacementMode.Curved;Check(RoomExtensionReader.SelectWallTool(H)&&H.Player.WallPlacementMode==WallPlacementMode.Straight,"native mode selector exits curved wall mode");
   t=Front(out room);Settings.Get<BuildModeCatalogTags>().Tags.Clear();Check(!RoomExtensionReader.SelectWallTool(H)&&UI.Catalog.Calls==0,"missing wall tag does not fabricate a native tool");
   t=Front(out room);ModInputKey.Available=false;RoomExtensionReader.OpenDepth(H,t);Check(UI.Form.IsVisible&&CoordinateEntryHandler.DescribeForm(UI.Form).Contains("extension_depth_instructions")&&!CoordinateEntryHandler.DescribeForm(UI.Form).Contains("4; 0"),"F7 opens a single depth field without claiming F10 or requiring a second number");
   UI.Form.Submit("3");Time.frameCount++;CoordinateEntryHandler.Tick();Check(WallConstructionHandler.Started!=null&&WallConstructionHandler.Started.Depth==3&&H.ButtonConfirm.Releases==0,"single depth submission requests review without a wall purchase");
   t=Front(out room);RoomExtensionReader.OpenDepth(H,t);UI.Form.Submit("3; 4");Time.frameCount++;CoordinateEntryHandler.Tick();Check(WallConstructionHandler.Started==null&&ScreenReader.Spoken.Last().Contains("extension_width_fixed"),"a different requested width gets the explicit selected-wall-width reason");
   t=Front(out room);RoomExtensionReader.OpenDepth(H,t);UI.Form.Submit(t.Width.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+";3");Time.frameCount++;CoordinateEntryHandler.Tick();Check(WallConstructionHandler.Started!=null&&WallConstructionHandler.Started.Depth==3&&H.ButtonConfirm.Releases==0,"matching width/depth pair requests the same depth review without buying");
   t=Front(out room);RoomExtensionReader.OpenDepth(H,t);UI.Form.Hide();Time.frameCount++;CoordinateEntryHandler.Tick();Check(WallConstructionHandler.Started==null,"cancelling depth form does not start extension");
   t=Front(out room);RoomExtensionReader.OpenDepth(H,t);MeasurementUnitService.Unit.MetricMultiplier=3.28084f;CoordinateEntryHandler.Tick();Check(!UI.Form.IsVisible&&WallConstructionHandler.Started==null,"changed units cannot reinterpret an open depth form");
   return checks+" room extension planner, space, tool-selection and single-depth form checks passed.";
  }
 }
}
