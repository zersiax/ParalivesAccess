namespace ParalivesAccess.Handlers {
 public static partial class LotDescriptionChecks {
  static int checks,nextId;
  static PlacementGrid world;
  static HybridPlayer H=>PlayerManager.Instance.HybridPlayer1;
  static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
  static Vector3 P(double x,double y){world.World(x,y,out double wx,out double wz);return new Vector3((float)wx,0,(float)wz);}
  static bool Same(Vector3 a,Vector3 b)=>Vector3.Distance(a,b)<0.001f;
  static void Reset(double width=40,double depth=20,double angle=0,double x=0,double z=0){
   world=new PlacementGrid(x,z,angle,1);nextId=1;PlayerManager.Instance=new PlayerManager();
   ZoneManager.Instance=new ZoneManager();SegmentManager.Instance=new SegmentManager();ItemManager.Instance=new ItemManager();
   LotManager.Instance=new LotManager();LotManager.Instance.Lot.Grid.Rotation=(float)angle;LotManager.Instance.Lot.Grid.OffsetFromOrigin=new Vector2((float)x,(float)z);
   var perimeter=new LotPerimeterZoneObject();Boundary(perimeter,new[]{P(0,0),P(width,0),P(width,depth),P(0,depth)},false);ZoneManager.Instance.Perimeter=perimeter;
   H.HybridCamera.FreeCamera.LookTarget.position=P(width/2,depth/2);Keyboard.current=new Keyboard();
   ScreenReader.Said.Clear();Plugin.Log.Lines.Clear();GameLoadingManager.State=State.Game;BuildModeRefreshManager.IsZoneStateRefreshed=true;
   ModInputKey.Available=true;ItemPlacementHandler.Lot=LotObjectHandler.Lot=1;LotDescriptionHandler.DescribeKey=Key.J;
   MeasurementUnitService.Multiplier=1;MeasurementUnitService.Unit="m";LotBuildInputOwnership.OwnsInput=false;
   SettingsHandler.IsCapturingBindingInput=CoordinateEntryHandler.BlocksPlacement=TownMapLotHandler.OwnsInput=false;
   BuildCatalogHandler.OwnsInput=TutorialHandler.OwnsInput=ContextActionHandler.OwnsInput=OccupationHandler.OwnsInput=false;
  }
  static void Boundary(ZoneObject zone,Vector3[] points,bool walls){
   zone.Segments.Clear();zone.InstanceID=nextId++;zone.AveragePosition=points.Aggregate(Vector3.zero,(a,b)=>a+b)/points.Length;
   for(int i=0;i<points.Length;i++){
    var a=points[i];var b=points[(i+1)%points.Length];
    var existing=walls?SegmentManager.Instance.Walls.OfType<SegmentWallObject>().FirstOrDefault(w=>w.Layer==zone.Layer&&
      ((Same(w.SegmentPoints.First(),a)&&Same(w.SegmentPoints.Last(),b))||(Same(w.SegmentPoints.First(),b)&&Same(w.SegmentPoints.Last(),a)))):null;
    if(existing!=null){zone.Segments.Add(new ZoneSegment{Segment=existing,FromAToB=Same(existing.SegmentPoints.First(),a)});continue;}
    SegmentObject segment=walls?new SegmentWallObject():new SegmentObject();segment.InstanceID=nextId++;segment.Layer=zone.Layer;
    segment.NodeA=Node(a);segment.NodeB=Node(b);segment.SegmentPoints.Add(a);segment.SegmentPoints.Add(b);
    if(segment is SegmentWallObject wall){
     var normal=new Vector3(-(b.z-a.z),0,b.x-a.x).normalized;
     var section=new SegmentWallSectionObject{InstanceID=nextId++,Wall=wall};wall.WallSections.Add(section);
     section.SideA=new SegmentWallSectionRenderer{WallSection=section,WallSide=WallVerticalSectionPartSides.SideA,Mesh=new Mesh{vertices=new[]{a,b,b+Vector3.up*3,a+Vector3.up*3},normals=new[]{normal}}};
     section.SideB=new SegmentWallSectionRenderer{WallSection=section,WallSide=WallVerticalSectionPartSides.SideB,Mesh=new Mesh{vertices=new[]{b,a,a+Vector3.up*3,b+Vector3.up*3},normals=new[]{normal*-1}}};
     SegmentManager.Instance.Walls.Add(wall);
    }
    zone.Segments.Add(new ZoneSegment{Segment=segment});
   }
  }
  static NodeObject Node(Vector3 p)=>SegmentManager.Instance.Walls.SelectMany(w=>new[]{w.NodeA,w.NodeB}).FirstOrDefault(n=>n!=null&&Same(n.Position,p))??new NodeObject{Position=p};
  static ZoneWallObject Room(double left,double front,double right,double back,int layer=0){
   var room=new ZoneWallObject{Layer=layer};Boundary(room,new[]{P(left,front),P(right,front),P(right,back),P(left,back)},true);ZoneManager.Instance.Zones.Add(room);return room;
  }
  static ItemObjectRoot Item(string name,double x,double z,ZoneWallObject room=null){
   var item=new ItemObjectRoot{InstanceID=nextId++,Name=name,FloorPlacedOn=room==null?null:new ZoneWallPlatformObject{WallZone=room},Layer=room?.Layer??0};
   item.transform.position=item.CubeTransform.LocalPosition=P(x,z);item.transform.rotation=new Quaternion{Angle=(float)(world.Rotation*Math.PI/180)};
   ItemManager.Instance.Items.Add(item);return item;
  }
  static string Press(bool shift=false){ScreenReader.Said.Clear();Keyboard.current.Pressed=Key.J;Keyboard.current.leftShiftKey.isPressed=shift;LotDescriptionHandler.Tick();Keyboard.current.Pressed=Key.None;return ScreenReader.Said.LastOrDefault()??"";}
  static SpatialOutline Outline(){Check(PlacementGridReader.TryRead(1,LotManager.Instance.Lot.Grid.CellDimension,out var grid)&&SpatialOutline.TryRead(ZoneManager.Instance.Perimeter,grid,out _),"native outline readable");SpatialOutline.TryRead(ZoneManager.Instance.Perimeter,grid,out var outline);return outline;}
  static string RoomReport(ZoneWallObject room){PlacementGridReader.TryRead(1,1,out var grid);var rooms=RoomSpatialReader.OrderedRooms(1,room.Layer);return LotLayoutReader.DescribeRoom(room,rooms.IndexOf(room),rooms,grid,Outline());}
  public static string Run(){
   Reset(74.8,74.8);H.HybridCamera.FreeCamera.LookTarget.position=P(26.74,28.51);
   Check(BuildCameraHandler.TryGetContext(H,out string report)&&report.Contains("about 14 m down-left")&&!report.Contains("near the lot centre"),"reported test coordinates no longer become the central third");
   report=Press();Check(report.Contains("square")&&report.Contains("74.8 m wide by 74.8 m")&&report.Contains("No enclosed rooms")&&!report.Contains("arrow")&&!report.Contains("building controls"),"empty-lot J provides scale and shape without repeated control instructions");
   Check(!report.Contains("middle of that overall span lies outside"),"square lot centre is inside even with a native ray through a corner");
   for(int i=0;i<600;i++)LotDescriptionHandler.Tick();Check(ScreenReader.Said.Count==1&&Plugin.Log.Lines.Count==1,"holding/no fresh key edge gives no speech or log flood");
   foreach(double angle in new[]{0,30,90,-45}){
    Reset(40,20,angle,213,-147);report=Press();Check(report.Contains("rectangular")&&report.Contains("40 m wide by 20 m"),"translated rotated lot retains physical dimensions "+angle);
    H.HybridCamera.FreeCamera.LookTarget.position=P(8,5);report=BuildCameraHandler.DescribeCamera(H,false);Check(report.Contains("13 m down-left"),"F3 uses fixed lot axes, never camera/world axes "+angle);
    LotManager.Instance.Lot.Grid.CellDimension=0.5f;Check(Press().Contains("40 m wide by 20 m"),"construction-cell spacing does not change physical size "+angle);
    var a=Room(2,2,8,6);var b=Room(8,2,12,6);report=RoomReport(a);Check(report.Contains("Shares right wall with room "),"shared-wall direction follows rotated lot frame "+angle);
   }
   Reset(1000,100);Check(Press().Contains("1000 m wide by 100 m"),"large-lot scale remains explicit");
   Reset();var per=ZoneManager.Instance.Perimeter;
   Boundary(per,new[]{P(0,0),P(20,0),P(40,0),P(40,20),P(0,20)},false);Check(Outline().Shape()=="rectangular","native collinear subdivisions do not add shape corners");
   Boundary(per,new[]{P(0,0),P(20,0),P(20,8),P(8,8),P(8,20),P(0,20)},false);
   report=Press();Check(report.Contains("L-shaped, with the back right corner cut out")&&report.Contains("20 m wide by 20 m")&&report.Contains("middle of that overall span lies outside"),"L-lot reports recess and does not imply its bounding centre is inside");
   H.HybridCamera.FreeCamera.LookTarget.position=P(7.5,12);report=BuildCameraHandler.DescribeCamera(H,false);Check(report.Contains("within 0.5 m of the lot boundary"),"F3 measures the actual concave boundary, not outer bounds");
   H.HybridCamera.FreeCamera.LookTarget.position=P(12,12);Check(BuildCameraHandler.DescribeCamera(H,false).Contains("outside"),"real concave cutout remains outside despite being within the enclosing rectangle");
   var reversed=per.Segments.Select(e=>e.Segment.SegmentPoints[0]).Reverse().ToArray();Boundary(per,reversed,false);Check(Outline().Shape().Contains("back right corner cut out"),"clockwise native winding gives the same L shape");
   per.Segments[0].Segment.IsCurved=true;Check(Outline().Shape()=="outline includes curved edges","curved edges are not claimed to be straight polygons");per.Segments[0].Segment.IsCurved=false;
   per.Segments[0].Segment.SegmentPoints[0]=P(5,5);Check(Press()=="The lot layout is not available yet","disconnected native boundary refuses an invented outline");
   Reset();per=ZoneManager.Instance.Perimeter;Boundary(per,new[]{P(0,0),P(20,0),P(10,10)},false);Check(Outline().Shape()=="3-sided outline","nonrectangular convex shape is not reduced to its bounding rectangle");
   per.Segments[0].Segment.SegmentPoints[0]=new Vector3(float.NaN,0,0);Check(Press()=="The lot layout is not available yet","nonfinite boundary is explicitly unavailable");
   Reset(74.8,74.8);var testedRoom=Room(36,36,40,40);H.HybridCamera.FreeCamera.LookTarget.position=P(38,38);
   Check(!MathService.ZoneObjectContainsPoint(testedRoom,P(38,38))&&ZoneGeometry.Contains(testedRoom,P(38,38)),"reported native double-corner failure is reproduced and corrected in production geometry");
   Check(Press().Contains("Camera focus in room 1")&&Press(true).StartsWith("Lot outline:"),"J describes the centred room while Shift+J describes the whole lot");
   Check(ZoneGeometry.Contains(testedRoom,P(36,36))&&ZoneGeometry.Contains(testedRoom,P(36,38))&&!ZoneGeometry.Contains(testedRoom,P(35.9,38)),"boundary corners and edges count as inside without admitting exterior points");
   var moving=Item("Cot",38,38,testedRoom);
   Check(PlacementLocationReader.Describe(moving).Contains("In room 1, middle centre"),"actual preview location reports its room and central region");
   moving.CubeTransform.LocalPosition=P(39.5,39.5);Check(PlacementLocationReader.Describe(moving).Contains("back right"),"preview movement reports its new part of the room");
   moving.CubeTransform.LocalPosition=P(38,46.53);Check(PlacementLocationReader.Describe(moving).StartsWith("Outdoors,"),"reported bed movement beyond room is explicitly outdoors");
   moving.CubeTransform.LocalPosition=P(80,38);Check(PlacementLocationReader.Describe(moving).Contains("outside"),"preview leaving lot is identified");
   moving.CubeTransform.LocalPosition=P(38,38);moving.Layer=1;Check(PlacementLocationReader.Describe(moving).StartsWith("Outdoors,"),"room below a preview is not announced as the same room");
   Check(!ZoneGeometry.Contains(testedRoom,new Vector3(float.NaN,0,38)),"nonfinite preview cannot be inside a room");
   Reset(20,20,0,-10,-10);Check(ZoneGeometry.Contains(ZoneManager.Instance.Perimeter,new Vector3(1,0,1)),"world origin inside lot does not invert descriptive membership");
   Reset();var main=Room(10,10,16,14);var right=Room(16,10,20,14);var front=Room(10,6,16,10);var corner=Room(20,14,23,17);
   report=RoomReport(main);Check(report.Contains("Shares right wall with room 2")&&report.Contains("Shares front wall with room 3")&&!report.Contains("with room 4"),"same-floor graph uses creation order including a newer front room and excludes corner-only contact");
   Check(RoomReport(right).Contains("Shares left wall with room 1"),"opposite side has reciprocal left relationship");
   Check(RoomReport(corner).Contains("No shared boundaries"),"detached rooms explicitly say they share no boundaries");
   Room(10,10,16,14,1);Check(!RoomReport(main).Contains("with room 5"),"upper floor does not become a horizontal neighbor");
   report=Press(true);Check(report.Contains("5 enclosed rooms")&&report.Contains("Floor layer 1"),"whole-lot overview groups rooms across floors");
   var shared=(SegmentWallObject)main.Segments[1].Segment;shared.WallSections[0].DisabledByUser=true;
   Check(RoomReport(main).Contains("boundary without a full visible wall with room 2")&&!RoomReport(main).Contains("Shares right wall"),"hidden separator is described as boundary rather than a full wall");shared.WallSections[0].DisabledByUser=false;
   ZoneManager.Instance.Zones.Remove(right);Check(!RoomReport(main).Contains("Shares right wall"),"removing a native room refreshes the graph immediately");ZoneManager.Instance.Zones.Add(right);
   H.HybridCamera.FreeCamera.LookTarget.position=P(12,12);Item("Sofa",10.5,12,main);var desk=Item("Desk",14,12,main);Item("Lamp",14,12,main).ItemPlacedOn=desk;
   var window=Item("Window",13,14,main);window.FloorPlacedOn=null;window.WallSectionPlacedOn=((SegmentWallObject)main.Segments[2].Segment).WallSections[0];window.WallSideOn=WallVerticalSectionPartSides.SideA;
   var outside=Item("Outside decoration",17,12,right);var preview=Item("Unplaced preview",12,12,main);H.Player.ItemInPlacement=new ItemObjectInPlacement{Item=preview};
   report=Press();Check(report.Contains("Camera focus in room 1")&&report.Contains("Sofa: middle left, against the left wall")&&report.Contains("Window: on the back wall"),"J reuses real room/object/attachment readers without requiring a reference wall");
   Check(report.Contains("Lamp: middle centre, on Desk")&&!report.Contains("Outside decoration")&&!report.Contains("Unplaced preview"),"contents respect native room ownership, attachments and preview exclusions");
   Check(!report.Contains("bounding gap")&&!report.Contains("section ")&&!report.Contains("starting endpoint")&&!report.Contains("column"),"ordinary room description avoids per-object coordinate and measurement diagnostics");
   Check(Press(true).StartsWith("Lot outline:")&&!ScreenReader.Said.Last().Contains("Sofa:"),"Shift+J reads the lot rather than dumping all room contents");
   var chosen=RoomSpatialReader.Faces(main).First(f=>f.Face.WallSection.Wall==main.Segments[3].Segment);
   RoomSpatialReader.SetReference(main,chosen);RoomSpatialReader.TryReference(main,out var originalReference);
   Check(Press().Contains("Sofa: middle left, against the left wall")&&RoomSpatialReader.TryReference(main,out var retainedReference)&&originalReference.Same(retainedReference),
    "J keeps its common lot directions without changing a separately chosen F7 front-wall reference");
   H.Player.ItemInPlacement=null;window.WallSideOn=WallVerticalSectionPartSides.SideB;Check(!Press().Contains("Window:"),"opposite wall-side attachment is not attributed to this room");window.CenterToWallThickness=true;Check(Press().Contains("Window:"),"through-wall door/window appears on both sides");
   window.CenterToWallThickness=false;window.WallSideOn=WallVerticalSectionPartSides.SideA;((SegmentWallObject)main.Segments[2].Segment).IsCurved=true;
   Check(Press().Contains("Attachments on curved or unsupported wall faces may be omitted"),"unsupported faces disclose the contents limit");
   H.Player.Layer=1;Check(Press().Contains("room 1, floor layer 1")&&!ScreenReader.Said.Last().Contains("Sofa:"),"inside-room lookup and contents use current floor");
   Reset();var loose=new ZoneWallObject();Boundary(loose,new[]{P(2,2),P(8,2),P(8,6)},true);SegmentManager.Instance.Walls.RemoveAt(2);
   report=Press();Check(report.Contains("No enclosed rooms")&&report.Contains("2 wall segments outside completed rooms"),"unfinished construction is distinguished from an empty lot");
   SegmentManager.Instance.Walls[0].PlacementPlayer=0;Check(Press().Contains("1 wall segments outside completed rooms"),"uncommitted native wall previews are excluded");
   Reset();MeasurementUnitService.Multiplier=3.28084f;MeasurementUnitService.Unit="ft";Check(Press().Contains("131.2 ft wide by 65.6 ft"),"lot measurements use native selected-unit formatting");
   H.HybridCamera.FreeCamera.LookTarget.position=P(8,5);Check(BuildCameraHandler.DescribeCamera(H,false).Contains("43 ft down-left"),"F3 rounds after native unit conversion");
   foreach(double rotation in new[]{0d,30d,90d,-45d}){
    Reset(20,20,rotation,213,-147);var facingRoom=Room(7,7,13,13);H.HybridCamera.FreeCamera.LookTarget.position=P(10,10);var facingDesk=Item("Facing desk",10,10,facingRoom);
    foreach(var heading in new[]{Tuple.Create(0d,"back wall"),Tuple.Create(Math.PI/2,"left wall"),Tuple.Create(Math.PI,"front wall"),Tuple.Create(-Math.PI/2,"right wall"),Tuple.Create(Math.PI/4,"back left corner"),Tuple.Create(-Math.PI/4,"back right corner"),Tuple.Create(3*Math.PI/4,"front left corner"),Tuple.Create(-3*Math.PI/4,"front right corner")}){
     facingDesk.transform.rotation=new Quaternion{Angle=(float)(rotation*Math.PI/180+heading.Item1)};
     string facingReport=Press();Check(facingReport.Contains("Facing desk: middle centre, facing the "+heading.Item2),"J reads native facing in the translated/rotated lot frame "+rotation+" "+heading.Item2);
    }
    facingDesk.transform.rotation=new Quaternion{Angle=(float)(rotation*Math.PI/180)};
    var referenceFrame=new WallFaceFrame{Origin=P(7,7),Right=P(8,7)-P(7,7),Normal=P(7,8)-P(7,7)};
    Check(RoomSpatialReader.DescribeContents(facingRoom,referenceFrame,false).Contains("facing the back wall"),"F7 detailed contents also report facing "+rotation);
    Check(!Press(true).Contains("Facing desk:"),"Shift+J keeps its brief room-outline overview "+rotation);
    var camera=H.HybridCamera.FreeCamera.LookTarget.position;var position=facingDesk.CubeTransform.LocalPosition;
    Press();Check(Same(camera,H.HybridCamera.FreeCamera.LookTarget.position)&&Same(position,facingDesk.CubeTransform.LocalPosition),"facing description is read-only "+rotation);
    facingDesk.transform.rotation=new Quaternion{Angle=float.NaN};Check(Press().Contains("facing unavailable"),"nonfinite native orientation is not guessed "+rotation);
   }
   Reset(4,4);H.HybridCamera.FreeCamera.LookTarget.position=P(2.3,2);Check(BuildCameraHandler.DescribeCamera(H,false).Contains("about 0.3 m right"),"small-lot offsets do not round to a misleading zero distance");
   Reset();H.Player.State=GameStates.PlaceSegment;H.Player.SegmentInPlacement=new object();LotBuildInputOwnership.OwnsInput=true;var before=H.HybridCamera.FreeCamera.LookTarget.position;
   Check(Press(true).StartsWith("Lot outline:")&&Same(before,H.HybridCamera.FreeCamera.LookTarget.position)&&H.Player.SegmentInPlacement!=null,"description remains read-only during native wall preview and exclusive build ownership");
   H.Player.State=GameStates.LiveMode;ItemPlacementHandler.Lot=0;Check(Press().StartsWith("Lot outline:"),"live mode uses existing camera-first explorer lot resolver");
   H.Player.State=GameStates.BuildMode;Check(Press()=="The lot layout is not available yet","build mode never falls through to a selected Para's unrelated lot");
   Reset();ZoneManager.Instance.DirtyWallZones.Add(1);Check(Press().Contains("updating the rooms"),"dirty geometry cannot announce a stale adjacency graph");ZoneManager.Instance.DirtyWallZones.Clear();BuildModeRefreshManager.IsZoneStateRefreshed=false;Check(Press().Contains("updating the rooms"),"native refresh gate applies before room lookup");BuildModeRefreshManager.IsZoneStateRefreshed=true;
   ModInputKey.Available=false;Check(Press()=="native binding conflict","native binding conflict refuses description");ModInputKey.Available=true;
   foreach(int gate in Enumerable.Range(0,15)){
    Reset();H.HasInputFieldFocused=gate==0;H.Player.PlayerActionBlockedByUIWindow=gate==1;SettingsHandler.IsCapturingBindingInput=gate==2;CoordinateEntryHandler.BlocksPlacement=gate==3;
    TownMapLotHandler.OwnsInput=gate==4;BuildCatalogHandler.OwnsInput=gate==5;TutorialHandler.OwnsInput=gate==6;ContextActionHandler.OwnsInput=gate==7;OccupationHandler.OwnsInput=gate==8;
    Keyboard.current.leftCtrlKey.isPressed=gate==9;Keyboard.current.rightAltKey.isPressed=gate==10;Keyboard.current.leftMetaKey.isPressed=gate==11;
    if(gate==12)GameLoadingManager.State=State.Loading;if(gate==13)H.Player.State=GameStates.CharacterCreator;if(gate==14)LotDescriptionHandler.DescribeKey=Key.None;
    Check(Press()=="","no descriptions under unrelated owner/modifier/loading gate "+gate);
   }
   return checks+" lot-description checks passed, including actual localized speech. Unity timing and screen-reader playback remain unverified.";
  }
 }
}
