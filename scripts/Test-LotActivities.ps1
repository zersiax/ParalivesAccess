$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\LotActivityReader.cs')
# Complete production reader against native-data adapters. No UI creation, action execution,
# save mutation or inventory-loading API is provided to the reader. Interior enumeration throws.
$adapters = @'
namespace Setting {
 public enum InteractionItemType {Interaction,Group}
 public enum HowToDisplayIfNotMet {Hide,GreyOut}
 public class UlongAndGuid {public ulong Value;}
 public class InteractionUsabilityRule {public HowToDisplayIfNotMet DisplayRule;public string GreyedOutTooltip;}
 public class InteractionUnit {public ulong GUID,CharacterRequirement;public string DisplayName;public string TranslationKey=>"Interaction_"+DisplayName;}
 public class InteractionGroupItem {public InteractionItemType Type;public ulong Interaction,Group;public bool IsNestedNameDifferentThanInteractionName;public string DisplayNameOfNestedInteraction;public string NestedNameTranslationKey=>"NestedInteraction_"+DisplayNameOfNestedInteraction;}
 public class InteractionGroup {public ulong GUID,SkinTagGUID;public string DisplayName;public bool DisplayNameIsSkinnable,DisplayNameIsImpostorLotType;public InteractionGroupItem[] ChildrenInteractionAndGroups;public string TranslationKey=>"InteractionGroup_"+DisplayName;}
 public class Interactions {
  public Dictionary<ulong,InteractionUnit> Units=new Dictionary<ulong,InteractionUnit>();public Dictionary<ulong,InteractionGroup> Groups=new Dictionary<ulong,InteractionGroup>();public UlongAndGuid[] GlobalInteractions;
  public InteractionUnit GetInteractionByGUID(ulong id)=>Units.TryGetValue(id,out var value)?value:null;
  public InteractionGroup GetInteractionGroupByGUID(ulong id)=>Groups.TryGetValue(id,out var value)?value:null;
 }
 public class LotType {public float OpeningHoursStart,OpeningHoursEnd=1440;public bool Open=true;public bool IsOpen()=>Open;public string TranslationKey="LotType_Shop";}
 public class LotTypes {public LotType Current=new LotType();public LotType GetLotTypeByGUID(ulong id)=>Current;}
 public class GeneralOptions {public bool Hour24Mode=true;}
 public class SkinProperty {public string StringValue;}
 public class Skin {public SkinProperty Property;public SkinProperty GetProperty(ulong id)=>Property;}
 public class Skins {public Skin Current;public Skin GetSkinByGUID(ulong id)=>Current;}
}
public class GameObject {public ItemObjectRoot Root;public T GetComponent<T>() where T:class=>Root as T;}
public class AssetLotData {public ulong LotTypeGUID=1;}
public class AssetLot {public ulong GUID=1;public AssetLotData Data=new AssetLotData();public GameObject ImpostorObject;}
public class ItemObjectRoot {public bool Live=true,MenuAvailable=true;public int InstanceID;public ulong LotPlacedOnGUID=1,CurrentSkinGUID,IsImpostorForLot;public Setting.InteractionGroup Group;public Dictionary<ulong,List<Setting.InteractionUsabilityRule>> Failures=new Dictionary<ulong,List<Setting.InteractionUsabilityRule>>();}
public enum GameStates {LiveMode,Other}
public class Placement {public ItemObjectRoot Item;}
public class Player {public GameStates State;public bool IsInIntro;public int PlayerIndex;public List<ulong> SelectedCharactersGUID=new List<ulong>{10,11,12};public Placement ItemInPlacement;}
public class HybridPlayer {public Player Player=new Player();}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public class LotManager {public static LotManager Instance=new LotManager();public AssetLot Lot;public AssetLot GetLotByGUID(ulong id)=>Lot!=null&&id==Lot.GUID?Lot:null;}
public class ItemManager {public static ItemManager Instance=new ItemManager();public List<ItemObjectRoot> Roots=new List<ItemObjectRoot>();public List<ItemObjectRoot> GetLotItems(ulong id)=>throw new Exception("Lot previews must not enumerate interior objects");}
public class CharacterManager {public static CharacterManager Instance=new CharacterManager();public HashSet<ulong> Blocked=new HashSet<ulong>();public int SelectedCount;public bool AnyCharacterHasCharacterRequirement(ulong[] selected,ulong requirement){SelectedCount=selected.Length;return !Blocked.Contains(requirement);}}
public class InteractionManager {
 public static InteractionManager Instance=new InteractionManager();public HashSet<ulong> HiddenGroups=new HashSet<ulong>();public Dictionary<ulong,ulong> LotArguments=new Dictionary<ulong,ulong>();public ulong OtherCharacter;
 public Setting.InteractionGroup GetItemInteractionGroup(ItemObjectRoot root)=>root.Group;
 public bool ItemHasInteractions(ItemObjectRoot root,int player)=>root.MenuAvailable;
 public bool CanShowInteractionGroupInInteractionList(Setting.InteractionGroup group,ItemObjectRoot root,int player,ulong character,ulong other,ulong lot)=>!HiddenGroups.Contains(group.GUID);
 public List<Setting.InteractionUsabilityRule> GetFaillingInteractionUsabilityRule(Setting.InteractionUnit unit,ItemObjectRoot root,int inventory,int player,ulong character,ulong other,ulong lot){LotArguments[unit.GUID]=lot;OtherCharacter=other;return root.Failures.TryGetValue(unit.GUID,out var failures)?failures:new List<Setting.InteractionUsabilityRule>();}
}
public static class Settings {
 public static Setting.Interactions Interactions=new Setting.Interactions();public static Setting.LotTypes LotTypes=new Setting.LotTypes();public static Setting.GeneralOptions Options=new Setting.GeneralOptions();public static Setting.Skins Skins=new Setting.Skins();
 public static T Get<T>() where T:class=>(Interactions as T)??(LotTypes as T)??(Options as T)??(Skins as T);
}
public static class TranslationManager {public static Dictionary<string,string> Text=new Dictionary<string,string>();public static string Get(string key)=>Text.TryGetValue(key,out var value)?value:key;}
public static class FormatService {public static string GetTimeOfDay(float minutes,bool hour24)=>(hour24?"24h:":"12h:")+minutes;}
namespace ParalivesAccess {
 public static class Loc {public static string Get(string key,params object[] args)=>key=="lot_detail_separator"?". ":key=="list_separator"?"; ":key+":"+string.Join("|",args);}
 public class Logger {public void LogError(string text){throw new Exception(text);}}
 public static class Plugin {public static Logger Log=new Logger();}
}
namespace ParalivesAccess.Speech {public static class ScreenReader {public static string NormalizeForOutput(string text)=>text==null?null:Regex.Replace(text,"<[^>]+>","").Trim();}}
namespace ParalivesAccess.Handlers {
 public static class WorldTargeting {public static bool IsSameLiveItem(ItemObjectRoot root,int id,ulong lot)=>root.Live&&root.InstanceID==id&&(lot==0||root.LotPlacedOnGUID==lot);}
 public static class LotActivityChecks {
  static int checks;static ulong next;
  static void Check(bool condition,string name){if(!condition)throw new Exception(name);checks++;}
  static InteractionGroupItem Action(string text,ulong requirement=0){ulong id=++next;Settings.Interactions.Units[id]=new InteractionUnit{GUID=id,DisplayName="a"+id,CharacterRequirement=requirement};TranslationManager.Text["Interaction_a"+id]=text;return new InteractionGroupItem{Interaction=id};}
  static InteractionGroup Group(string text,params InteractionGroupItem[] children){ulong id=++next;var group=new InteractionGroup{GUID=id,DisplayName="g"+id,ChildrenInteractionAndGroups=children};Settings.Interactions.Groups[id]=group;TranslationManager.Text[group.TranslationKey]=text;return group;}
  static ItemObjectRoot Root(int id,InteractionGroup group){var root=new ItemObjectRoot{InstanceID=id,Group=group};ItemManager.Instance.Roots.Add(root);return root;}
  static int Occurrences(string text,string part)=>(text.Length-text.Replace(part,"").Length)/part.Length;
  public static string Run(){
   var lot=new AssetLot();LotManager.Instance.Lot=lot;TranslationManager.Text["LotType_Shop"]="Shop";
   Root(1,Group("Recipe Book",Action("Learn a random new recipe")));
   Root(2,Group("Piano",Action("Play piano")));
   Check(string.IsNullOrEmpty(LotActivityReader.Describe(lot)),"loaded lot preview never enumerates real interior objects or their actions");
   var visit=Action("Visit");var facade=Group("Building",visit);facade.DisplayNameIsImpostorLotType=true;
   var root=new ItemObjectRoot{InstanceID=20,IsImpostorForLot=1,LotPlacedOnGUID=999,Group=facade};lot.ImpostorObject=new GameObject{Root=root};
   string result=LotActivityReader.Describe(lot);
   Check(result.Contains("Visit")&&result.Contains("Shop")&&!result.Contains("Play piano")&&!result.Contains("Learn a random new recipe"),"preview reads only its exact exterior building menu and native heading");
   Check(!result.Contains("lot_activity_hours"),"native all-day hours remain omitted");
   Check(InteractionManager.Instance.LotArguments[visit.Interaction]==999,"impostor root uses native clicked-lot context");
   Check(CharacterManager.Instance.SelectedCount==3&&InteractionManager.Instance.OtherCharacter==11,"full selected group and secondary actor match native menu requirement inputs");
   root.MenuAvailable=false;Check(!LotActivityReader.Describe(lot).Contains("Visit"),"native menu-opening eligibility is respected before listing any rows");root.MenuAvailable=true;
   TranslationManager.Text["InteractionUsabilityRuleTooltip_Closed"]="Closed to visitors";
   root.Failures[visit.Interaction]=new List<InteractionUsabilityRule>{new InteractionUsabilityRule{DisplayRule=HowToDisplayIfNotMet.GreyOut,GreyedOutTooltip="Closed"}};
   Check(LotActivityReader.Describe(lot).Contains("Closed to visitors"),"greyed building menu rows retain their native hover reason");
   root.Failures[visit.Interaction].Add(new InteractionUsabilityRule{DisplayRule=HowToDisplayIfNotMet.Hide});
   Check(!LotActivityReader.Describe(lot).Contains("Visit"),"Hide dominates a simultaneous greyed-out failure exactly as native menu rows");
   root.Failures.Clear();var secret=Action("Secret interaction",99);facade.ChildrenInteractionAndGroups=new[]{visit,secret};CharacterManager.Instance.Blocked.Add(99);
   Check(!LotActivityReader.Describe(lot).Contains("Secret interaction"),"character requirement excludes hidden offerings");
   var service=Action("Exterior service");var nested=Group("Services",service);facade.ChildrenInteractionAndGroups=new[]{visit,new InteractionGroupItem{Type=InteractionItemType.Group,Group=nested.GUID}};
   result=LotActivityReader.Describe(lot);Check(result.Contains("Exterior service")&&InteractionManager.Instance.LotArguments[service.Interaction]==0,"nested native building menus use their own zero lot argument");
   InteractionManager.Instance.HiddenGroups.Add(nested.GUID);Check(!LotActivityReader.Describe(lot).Contains("Exterior service"),"native nested-group visibility is respected");
   var global=Action("Global building option");Settings.Interactions.GlobalInteractions=new[]{new UlongAndGuid{Value=global.Interaction},new UlongAndGuid{Value=global.Interaction}};
   Check(Occurrences(LotActivityReader.Describe(lot),"Global building option")==1,"building global menu rows are included and duplicate rows collapse");
   var named=Action("Generic name");named.IsNestedNameDifferentThanInteractionName=true;named.DisplayNameOfNestedInteraction="Special";TranslationManager.Text[named.NestedNameTranslationKey]="Named building option";facade.ChildrenInteractionAndGroups=new[]{named};
   Check(LotActivityReader.Describe(lot).Contains("Named building option")&&!LotActivityReader.Describe(lot).Contains("Generic name"),"native nested label override is preserved");
   TranslationManager.Text[named.NestedNameTranslationKey]="Give {0}";Check(!LotActivityReader.Describe(lot).Contains("Give {0}"),"unresolved dynamic parameters are not guessed or spoken");
   TranslationManager.Text.Remove(named.NestedNameTranslationKey);Check(!LotActivityReader.Describe(lot).Contains("NestedInteraction_"),"missing translations do not expose internal keys");
   facade.ChildrenInteractionAndGroups=new[]{visit};
   var player=PlayerManager.Instance.HybridPlayer1.Player;player.ItemInPlacement=new Placement{Item=root};Check(!LotActivityReader.Describe(lot).Contains("Visit"),"placement previews are excluded");player.ItemInPlacement=null;
   root.IsImpostorForLot=2;Check(string.IsNullOrEmpty(LotActivityReader.Describe(lot)),"foreign building cannot supply another lot's preview");root.IsImpostorForLot=1;
   root.Live=false;Check(string.IsNullOrEmpty(LotActivityReader.Describe(lot)),"inactive building cannot supply stale options");root.Live=true;
   var lotType=Settings.LotTypes.Current;lotType.OpeningHoursStart=480;lotType.OpeningHoursEnd=1320;lotType.Open=false;
   result=LotActivityReader.Describe(lot);Check(result.Contains("24h:480|24h:1320|lot_activity_closed"),"hours and closed state come from native formatting and IsOpen");
   Settings.Options.Hour24Mode=false;lotType.Open=true;Check(LotActivityReader.Describe(lot).Contains("12h:480|12h:1320|lot_activity_open"),"time preference and opening state are read live");
   player.SelectedCharactersGUID.Clear();result=LotActivityReader.Describe(lot);Check(result.Contains("lot_activity_hours")&&!result.Contains("Visit"),"hours remain readable without selected actors while actor-dependent offerings are omitted");
   player.SelectedCharactersGUID.Add(10);player.IsInIntro=true;Check(!LotActivityReader.Describe(lot).Contains("Visit"),"activity preview cannot bypass tutorial item restrictions");
   player.IsInIntro=false;lot.ImpostorObject=null;result=LotActivityReader.Describe(lot);
   Check(result.Contains("lot_activity_hours")&&!result.Contains("lot_activity_offerings"),"loaded lot retains hours without replacing its missing exterior menu with interior actions");
   Check(!result.Contains("Play piano")&&!result.Contains("Learn a random new recipe")&&!result.Contains("Global building option"),"loaded objects and their global options remain outside the preview");
   Check(LotActivityReader.Describe(null)==null,"missing lot returns no stale activities");
   return checks+" native lot-preview checks passed; rendered content and audible output remain runtime checks.";
  }
 }
}
'@
Add-Type -TypeDefinition ($source + "`n" + $adapters) -Language CSharp
[ParalivesAccess.Handlers.LotActivityChecks]::Run()
