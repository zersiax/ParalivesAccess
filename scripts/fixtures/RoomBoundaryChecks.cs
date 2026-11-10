namespace ParalivesAccess.Handlers {
 public static partial class LotDescriptionChecks {
  static ItemObjectInPlacement MovingAt(double x,double z,int layer=0){
   var item=new ItemObjectRoot{InstanceID=nextId++,Layer=layer};item.CubeTransform.LocalPosition=P(x,z);
   H.Player.ItemInPlacement=new ItemObjectInPlacement{Item=item};return H.Player.ItemInPlacement;
  }
  public static string RunBoundaries(string root){
   int before=checks;Reset(74.8,74.8);RoomBoundaryConstraint.Clear();RoomBoundaryConstraint.Enabled=false;RoomBoundaryConstraint.Available=true;
   RoomLabelHandler.Enabled=true;NewSave(root,Guid.NewGuid().ToString("N"));var room=Room(36,36,40,40);var adjacent=Room(40,36,44,40);Name(room,"Living room");Name(adjacent,"Kitchen");
   var preview=MovingAt(38,38);
   RoomBoundaryConstraint.Begin(preview,1,0,P(38,38));Check(RoomBoundaryConstraint.Problem(1,0,P(46,38))==null,"off by choice preserves free movement");
   Check(RoomBoundaryConstraint.Toggle(preview)=="Staying in Living room"&&RoomBoundaryConstraint.Option()=="Stay in Living room: on","toggle binds actual room with custom name and no reference-wall setup");
   Check(RoomBoundaryConstraint.Problem(1,0,P(38,38))==null,"native corner-ray defect cannot reject locked room centre");
   foreach(var p in new[]{P(36,36),P(36,38),P(40,38),P(38,36),P(38,40)})Check(RoomBoundaryConstraint.Problem(1,0,p)==null,"room boundary itself is reachable");
   foreach(var p in new[]{P(35.9,38),P(40.1,38),P(38,35.9),P(38,40.1)})Check(RoomBoundaryConstraint.Problem(1,0,p)?.Contains("Living room boundary")==true,"all four outward directions are refused");
   Check(RoomBoundaryConstraint.Problem(1,0,P(42,38))!=null,"adjoining room cannot silently replace the chosen room");
   Check(RoomBoundaryConstraint.Problem(2,0,P(38,38))!=null&&RoomBoundaryConstraint.Problem(1,1,P(38,38))!=null,"lot and floor changes fail closed");
   Check(RoomBoundaryConstraint.AllowsNativeCommit(H.Player),"valid native preview can commit");
   var safeCursor=new Vector2(123.25f,478.75f);var safeAim=P(38,38);
   Check(!RoomBoundaryConstraint.ObserveNative(preview,safeAim,safeCursor,out _,out _,out _),"indoor native result records its cursor without a correction");
   preview.Item.CubeTransform.LocalPosition=P(40.00236,38);
   Check(RoomBoundaryConstraint.ObserveNative(preview,P(40,38),new Vector2(200,478),out var restoreAim,out var restoreCursor,out var edgeProblem)
    &&Same(restoreAim,safeAim)&&restoreCursor.x==safeCursor.x&&restoreCursor.y==safeCursor.y&&edgeProblem.Contains("boundary"),"millimetre projection overshoot returns exact previously verified cursor input");
   Check(!RoomBoundaryConstraint.AllowsNativeCommit(H.Player)&&!RoomBoundaryConstraint.Describe().StartsWith("Staying"),"unrecovered native result cannot commit or claim it is staying indoors");
   preview.Item.CubeTransform.LocalPosition=P(38,38);
   Check(!RoomBoundaryConstraint.ObserveNative(preview,safeAim,safeCursor,out _,out _,out _)&&RoomBoundaryConstraint.AllowsNativeCommit(H.Player),"verified return restores commit without relaxing containment");
   ZoneManager.Instance.DirtyWallZones.Add(1);
   Check(!RoomBoundaryConstraint.ObserveNative(preview,safeAim,safeCursor,out _,out _,out _),"a stale room cannot replay a former cursor sample");ZoneManager.Instance.DirtyWallZones.Clear();
   preview.Item.CubeTransform.LocalPosition=P(38,46.53);
   Check(!RoomBoundaryConstraint.AllowsNativeCommit(H.Player),"native snap or mouse movement outdoors cannot commit");
   Check(RoomBoundaryConstraint.AllowsNativeCommit(new Player{ItemInPlacement=preview}),"room guard affects only its owning player");
   var replacement=MovingAt(38,46.53);Check(RoomBoundaryConstraint.AllowsNativeCommit(H.Player),"a replaced preview cannot inherit a stale commit guard");H.Player.ItemInPlacement=preview;
   var unchanged=preview.Item.CubeTransform.LocalPosition;
   Check(RoomBoundaryConstraint.Toggle(preview).Contains("Room boundary off")&&!RoomBoundaryConstraint.Enabled&&RoomBoundaryConstraint.AllowsNativeCommit(H.Player)&&Same(unchanged,preview.Item.CubeTransform.LocalPosition),"toggle off restores movement without moving or buying the preview");
   Check(RoomBoundaryConstraint.Toggle(preview).Contains("inside a room")&&!RoomBoundaryConstraint.Enabled,"enabling outdoors explains why there is no room and stays off");
   preview.Item.CubeTransform.LocalPosition=P(38,38);RoomBoundaryConstraint.Toggle(preview);
   ZoneManager.Instance.DirtyWallZones.Add(1);Check(RoomBoundaryConstraint.Problem(1,0,P(38,38)).Contains("updating")&&!RoomBoundaryConstraint.AllowsNativeCommit(H.Player),"dirty room geometry pauses movement and commit");ZoneManager.Instance.DirtyWallZones.Clear();
   foreach(var edge in room.Segments)for(int i=0;i<edge.Segment.SegmentPoints.Count;i++)if(Same(edge.Segment.SegmentPoints[i],P(36,36)))edge.Segment.SegmentPoints[i]=P(35,36);
   Check(RoomBoundaryConstraint.Problem(1,0,P(38,38)).Contains("room has changed"),"changed contour cannot silently redefine room confinement");
   RoomBoundaryConstraint.Toggle(preview);RoomBoundaryConstraint.Toggle(preview);Check(RoomBoundaryConstraint.Problem(1,0,P(38,38))==null,"off then on deliberately accepts the changed outline");
   ZoneManager.Instance.Zones.Remove(room);Check(!RoomBoundaryConstraint.AllowsNativeCommit(H.Player),"deleted native room refuses commit");
   RoomBoundaryConstraint.Clear();Check(RoomBoundaryConstraint.Enabled,"toggle choice survives the end of a preview");
   preview=MovingAt(42,38);RoomBoundaryConstraint.Begin(preview,1,0,P(42,38));Check(RoomBoundaryConstraint.Option()=="Stay in Kitchen: on"&&RoomBoundaryConstraint.Problem(1,0,P(38,38))!=null,"new preview uses its own room instead of retaining the previous room");
   preview=MovingAt(50,50);RoomBoundaryConstraint.Begin(preview,1,0,P(50,50));Check(RoomBoundaryConstraint.Describe().StartsWith("Outdoor placement")&&RoomBoundaryConstraint.Problem(1,0,P(60,60))==null,"outdoor preview remains free with an explicit initial announcement");
   foreach(double angle in new[]{0,30,90,-45}){
    Reset(30,30,angle,213,-147);var l=Room(1,1,9,9);Boundary(l,new[]{P(1,1),P(9,1),P(9,4),P(4,4),P(4,9),P(1,9)},true);
    preview=MovingAt(2,2);RoomBoundaryConstraint.Begin(preview,1,0,P(2,2));
    Check(RoomBoundaryConstraint.Problem(1,0,P(2,7))==null&&RoomBoundaryConstraint.Problem(1,0,P(7,2))==null&&RoomBoundaryConstraint.Problem(1,0,P(7,7))!=null,"rotated L room admits both wings and rejects the bounding-box cutout "+angle);
   }
   Reset();room=Room(2,2,8,8);preview=MovingAt(4,4);BuildModeRefreshManager.IsZoneStateRefreshed=false;
   RoomBoundaryConstraint.Begin(preview,1,0,P(4,4));Check(RoomBoundaryConstraint.Problem(1,0,P(4,4)).Contains("updating"),"new preview waits for room data instead of silently disabling the enabled toggle");
   BuildModeRefreshManager.IsZoneStateRefreshed=true;Check(RoomBoundaryConstraint.Problem(1,0,P(4,4))==null&&RoomBoundaryConstraint.Problem(1,0,P(10,10))!=null,"pending preview binds once native rooms settle");
   preview.Item.IsWallItem=true;RoomBoundaryConstraint.Begin(preview,1,0,P(4,4));Check(RoomBoundaryConstraint.Problem(1,0,P(10,10))==null,"wall previews retain their separate wall-face workflow");
   RoomBoundaryConstraint.Enabled=false;RoomBoundaryConstraint.Available=false;preview.Item.IsWallItem=false;
   Check(RoomBoundaryConstraint.Toggle(preview).Contains("unavailable")&&!RoomBoundaryConstraint.Enabled,"unverified native hook cannot enable the toggle");
   RoomBoundaryConstraint.Clear();RoomBoundaryConstraint.Available=true;
   return (checks-before)+" room-boundary geometry, lifecycle and native commit checks passed; Unity snapping and audible edge feedback require gameplay validation.";
  }
 }
}
