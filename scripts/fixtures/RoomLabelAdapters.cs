namespace UnityEngine {
 public static class Time { public static int frameCount; }
}
namespace HarmonyLib {
 public class HarmonyMethod { public System.Reflection.MethodInfo Method; public HarmonyMethod(System.Reflection.MethodInfo m){Method=m;} }
 public class Patch { public string owner; public System.Reflection.MethodInfo PatchMethod; }
 public class Patches { public List<Patch> Prefixes=new List<Patch>(),Postfixes=new List<Patch>(); }
 public class Harmony {
  static Patches patches;
  public void Patch(System.Reflection.MethodInfo m,HarmonyMethod prefix=null,HarmonyMethod postfix=null){patches=new Patches();patches.Prefixes.Add(new Patch{owner="test",PatchMethod=prefix.Method});patches.Postfixes.Add(new Patch{owner="test",PatchMethod=postfix.Method});}
  public static Patches GetPatchInfo(System.Reflection.MethodInfo m)=>patches;
 }
 public static class AccessTools {
  public static System.Reflection.MethodInfo Method(Type t,string name,Type[] types=null)=>t.GetMethod(name,System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Instance,null,types??Type.EmptyTypes,null)
   ?? t.GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Instance).FirstOrDefault(m=>m.Name==name);
 }
}
public class AssetSavedGame { public string FilePath; }
public class SavedGameManager { public static SavedGameManager Instance=new SavedGameManager(); public AssetSavedGame CurrentSavedGame; }
public enum GameSavingPhase {Init,SaveLotFetchData,SaveDirtyAssetsInTown,CopySaveAsDefaultTown,ShowEndFloatingText,Completed}
public class GameSavingRequest { public GameSavingPhase CurrentPhase; public AssetLot CurrentLot; }
public class GameSavingManager { protected void UpdateRequest(GameSavingRequest request){} }
public partial class AssetLot { public ulong GUID=1; public AssetLotData Data=new AssetLotData(); }
public class AssetLotData { public List<AssetLotDataZone> Zones=new List<AssetLotDataZone>(); }
public class AssetLotDataZone {public int InstanceID,Layer;public List<AssetSegmentDataZoneSegment> SegmentInstanceIDs=new List<AssetSegmentDataZoneSegment>();}
public class AssetSegmentDataZoneSegment {public int SegmentEntityInstanceID;public bool FromAToB;}
namespace ParalivesAccess.Handlers {
 public static partial class CoordinateEntryHandler {
  internal static Func<bool> Valid; internal static Action<string> Submit; internal static string Initial;
  internal static void OpenText(HybridPlayer h,string title,string hint,string instructions,string initial,Func<bool> valid,Action<string> apply){Valid=valid;Submit=apply;Initial=initial;}
 }
}
