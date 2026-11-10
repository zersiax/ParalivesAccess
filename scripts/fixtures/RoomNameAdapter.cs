// Unnamed-room dependency for suites devoted to other build controllers. The complete
// label resolver, persistence and layout integration run in Test-LotDescription.ps1.
namespace ParalivesAccess.Handlers {
 internal static class RoomLabelHandler {
  internal static string Name(ZoneWallObject room,int ordinal=0,int count=0)=>Loc.Get("build_camera_room",ordinal>0?ordinal:1);
 }
}
