namespace ParalivesAccess.Handlers {
 public static partial class LotDescriptionChecks {
  private sealed class AreaModel {internal ZoneWallObject Room;internal int RoomOrdinal,RoomCount;}
  private sealed class RoomOrderFloor {
   internal List<AreaModel> Areas=new List<AreaModel>();
   F7_ROOM_ORDINALS
  }
  public static string RunRoomDirections(){
   int before=checks;
   foreach(double angle in new[]{0d,30d,90d,180d,270d}){
    Reset(40,20,angle,100,-30);
    var room=Room(10,10,16,14);var faces=RoomSpatialReader.Faces(room);
    var expected=new[]{"front wall","right wall","back wall","left wall"};
    for(int i=0;i<4;i++){
     Check(faces[i].TryRead(1,0,out var face),"native face readable for direction test");
     Check(RoomSpatialReader.LotSide(1,face)==expected[i],"room extension direction follows rotated lot axes "+angle+" side "+i);
     Check(RoomSpatialReader.DescribeSide(faces[i],face).Contains(expected[i])&&RoomSpatialReader.DescribeSide(faces[i],face).Contains(RoomLabelHandler.Name(room)),"wall selection includes lot-relative side and room name "+angle+" side "+i);
    }
    RoomSpatialReader.SetReference(room,faces[1]);faces[0].TryRead(1,0,out var front);
    Check(RoomSpatialReader.LotSide(1,front)=="front wall","chosen advanced reference cannot change ordinary extension orientation");
    var normals=new[]{P(1,1)-P(0,0),P(-1,1)-P(0,0),P(1,-1)-P(0,0),P(-1,-1)-P(0,0)};
    var diagonals=new[]{"angled front-left wall","angled front-right wall","angled back-left wall","angled back-right wall"};
    for(int i=0;i<4;i++)Check(RoomSpatialReader.LotSide(1,new WallFaceFrame{Normal=normals[i].normalized})==diagonals[i],"diagonal wall retains an outward side in rotated lot "+angle);
    LotManager.Instance.Lot.Grid.Rotation=float.NaN;
    Check(RoomSpatialReader.LotSide(1,front)=="direction unavailable","invalid lot frame cannot invent a wall direction");
   }
   Reset();var first=Room(10,10,16,14);var left=Room(6,10,10,14);var frontRoom=Room(10,6,16,10);
   var floor=new RoomOrderFloor();
   floor.Areas.Add(new AreaModel{Room=frontRoom});floor.Areas.Add(new AreaModel());floor.Areas.Add(new AreaModel{Room=left});floor.Areas.Add(new AreaModel{Room=first});
   floor.AssignRoomOrdinals();
   Check(floor.Areas[3].RoomOrdinal==1&&floor.Areas[2].RoomOrdinal==2&&floor.Areas[0].RoomOrdinal==3,"actual F7 ordinal assignment appends new left and front rooms in creation order");
   Check(floor.Areas.Where(a=>a.Room!=null).All(a=>a.RoomCount==3)&&floor.Areas[1].RoomCount==0,"F7 counts every enclosed room and excludes outdoors");
   int savedId=first.InstanceID;var restored=new ZoneWallObject{InstanceID=savedId,Layer=first.Layer,Segments=first.Segments,AveragePosition=new Vector3(100,0,100)};
   ZoneManager.Instance.Zones.Remove(first);ZoneManager.Instance.Zones.Add(restored);floor.Areas[3].Room=restored;floor.Areas.Reverse();floor.AssignRoomOrdinals();
   Check(floor.Areas.Single(a=>a.Room==restored).RoomOrdinal==1&&RoomSpatialReader.OrderedRooms(1,0)[0]==restored,"restored saved ID preserves numbering despite object replacement, position and enumeration changes");
   Reset();var targetRoom=Room(10,10,16,14);var target=new ItemObjectRoot{Layer=0,CubeTransform=new ItemCubeTransform{LocalPosition=P(14,13)}};
   var preview=new ItemObjectRoot{Layer=0,CubeTransform=new ItemCubeTransform{LocalPosition=P(11,11)}};
   string location=PlacementLocationReader.Target(target,preview);
   Check(location.Contains(RoomLabelHandler.Name(targetRoom))&&location.Contains("Column")&&location.Contains("row")&&location.Contains("floor 0")&&location.Contains("right")&&location.Contains("back"),"target location combines room region, floor, lot coordinates and relative direction");
   preview.Layer=1;Check(!PlacementLocationReader.Target(target,preview).Contains("From the preview"),"relative direction requires matching floor layers");
   return (checks-before)+" room direction and F7 ordering checks passed";
  }
 }
}
