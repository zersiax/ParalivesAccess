// Other controller suites supply containment outcomes. Actual room geometry, identity,
// names and commit isolation are tested with production RoomBoundaryConstraint in LotDescription.
namespace ParalivesAccess.Handlers {
 internal static class RoomBoundaryConstraint {
  internal static bool Available=true,Enabled,CommitAllowed=true;
  internal static Func<ulong,int,Vector3,string> Evaluate {get;set;}
  internal static string Problem(ulong lot,int layer,Vector3 p)=>Evaluate?.Invoke(lot,layer,p);
  internal static string Describe()=>Enabled?"Staying in room":"";
  internal static string Option()=>Loc.Get(Enabled?"room_stay_on":"room_stay_off","Room");
  internal static string Toggle(ItemObjectInPlacement preview){Enabled=!Enabled;return Loc.Get(Enabled?"room_stay_enabled":"room_stay_disabled","Room");}
  internal static bool AllowsNativeCommit(Player player)=>CommitAllowed;
  internal static void Clear(){}
 }
}
