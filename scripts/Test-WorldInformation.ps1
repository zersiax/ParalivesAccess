$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$location = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\RelationshipLocationHandler.cs')
function Read-Method([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature)
    if ($start -lt 0) { throw "Missing production method: $signature" }
    $body = $source.IndexOf('{', $start)
    $depth = 1; $end = $body + 1
    while ($depth -gt 0) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    $source.Substring($start, $end - $start)
}
$lotSource = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\LotReadinessReader.cs')
$lotMethod = Read-Method $lotSource 'public static string DescribeTownMapLot('
$lotInfoMethod = Read-Method $lotSource 'public static string DescribeLotInfo('
$identityMethod = Read-Method $lotSource 'private static void AddLotIdentity('
$townMap = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\TownMapLotHandler.cs')
$classification = Read-Method $townMap 'private void ReadClassification('
$shortLabel = Read-Method $townMap 'internal string GetShortLabel('
$compareLots = Read-Method $townMap 'private static int CompareLotNames('
$reader = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\UiLabelReader.cs')
$storyStart = $reader.IndexOf('            UIStorytellerSelectionItem storyteller =')
$storyEnd = $reader.IndexOf('            // Authoritative even when null:', $storyStart)
if ($storyStart -lt 0 -or $storyEnd -lt 0) { throw 'Missing storyteller production branch' }
$storyMethod = 'public static string Story(UnityEngine.Component source) {' + $reader.Substring($storyStart, $storyEnd - $storyStart) + ' return null; }'
# Test the production native-data readers against explicit state and translation adapters.
# This verifies which data is used, not the game's geometry, callbacks or audio output.
$adapters = @'
namespace UnityEngine {
 public class GameObject {public bool activeInHierarchy=true,activeSelf=true;}
 public class Component {
  public Component parent;
  public T GetComponentInParent<T>() where T:Component {for(Component c=this;c!=null;c=c.parent)if(c is T t)return t;return null;}
 }
 public struct Vector3 {public float x;public Vector3(float value){x=value;}}
}
namespace Setting {
 public class LotType {public ulong GUID;public bool CanPurchase,CanBeVisitedByPlayer;public string DisplayName;public string TranslationKey=>"LotType_"+DisplayName;}
 public class LotTypes {public bool CanBuyAllLotTypes;public Dictionary<ulong,LotType> Types=new Dictionary<ulong,LotType>();public LotType GetLotTypeByGUID(ulong id)=>Types.TryGetValue(id,out var value)?value:null;}
 public class Company {public string DisplayName;}
 public class Occupations {public Dictionary<ulong,Company> Companies=new Dictionary<ulong,Company>();public Company GetCompanyByGUID(ulong id)=>Companies.TryGetValue(id,out var company)?company:null;}
 public enum TownAutonomyDestinationState {Idle,HeadingToALot,OnAWalkAroundTown,HeadingOutOfTown,OutOfTown}
 public class Storyteller {public string DisplayName;}
}
namespace HarmonyLib {
 public class HarmonyMethod {public HarmonyMethod(Type t,string method){}}
 public class Harmony {
  List<MethodBase> patched=new List<MethodBase>();
  public void Patch(MethodInfo method,HarmonyMethod postfix){patched.Add(method);}
  public IEnumerable<MethodBase> GetPatchedMethods()=>patched;
 }
 public static class AccessTools {public static MethodInfo Method(Type type,string name)=>type.GetMethod(name);}
}
public class UIRelationships {public ulong RelationshipWithCharacter;public void OnClickFindParafolk(){}}
public class AssetCharacterData {public string FullName;public UnityEngine.Vector3 Position;public ulong CurrentTownAutonomyTargetLot;public Setting.TownAutonomyDestinationState TownAutonomyDestinationState;}
public class AssetCharacter {public AssetCharacterData Data=new AssetCharacterData();public bool IsInImpostorLot;}
public class AssetHouseholdData {public string Name;public string GetHouseholdName()=>Name;}
public class AssetHousehold {public AssetHouseholdData Data=new AssetHouseholdData();}
public class AssetManager {public static AssetManager Instance=new AssetManager();public AssetCharacter current;public AssetCharacter GetCharacter(ulong id)=>current;public object GetAsset(ulong id)=>null;}
public class LotPerimeterZoneObject {public string DisplayName,Description,NativeAddress;public string GetAddress()=>NativeAddress;public ulong RealLotGUID,HouseholdOwnerGUID;public float SurfaceArea;public UITownMapLotIcon UIWidget;}
public class TooltipOpenerSimple {public string TextToShow;}
public class UITownMapLotIcon {public ulong LotGUID;public TooltipOpenerSimple ToolTip;public UnityEngine.GameObject LabelForSale;}
public class TextAdapter {public string text;public UnityEngine.GameObject gameObject=new UnityEngine.GameObject();}
public class TranslatedText:TextAdapter {}
public class UILotInfo {
 public LotPerimeterZoneObject Perimeter;public ulong LotGUID;public ulong GetLotGUID()=>LotGUID;
 public TextAdapter LotTypeLabel,LotSizeLabel,LotHouseholdNameLabel;
 public UnityEngine.GameObject LotHouseholdContainer,ForSale;
 public TranslatedText BuyButtonText,MessageText;
}
public class AssetLotData {public ulong[] Companies;public ulong LotTypeGUID;}
public class AssetLot {public AssetLotData Data=new AssetLotData();}
public class LotManager {public static LotManager Instance=new LotManager();public Dictionary<ulong,AssetLot> Lots=new Dictionary<ulong,AssetLot>();public AssetLot GetLotByGUID(ulong id)=>Lots.TryGetValue(id,out var lot)?lot:null;}
public static class Settings {public static Setting.Occupations Occupations=new Setting.Occupations();public static Setting.LotTypes LotTypes=new Setting.LotTypes();public static T Get<T>() where T:class=>(Occupations as T)??(LotTypes as T);}
public class ZoneManager {
 public static ZoneManager Instance=new ZoneManager();
 public Dictionary<ulong,LotPerimeterZoneObject> Lots=new Dictionary<ulong,LotPerimeterZoneObject>();
 public Dictionary<float,LotPerimeterZoneObject> Positions=new Dictionary<float,LotPerimeterZoneObject>();
 public LotPerimeterZoneObject GetLotPerimeterZoneObjectByGUID(ulong id)=>Lots.TryGetValue(id,out var lot)?lot:null;
 public bool TryGetLotPerimeterZoneAtPosition(UnityEngine.Vector3 p,out LotPerimeterZoneObject lot)=>Positions.TryGetValue(p.x,out lot);
}
public class UIStorytellerSelectionItem:UnityEngine.Component {public Setting.Storyteller Storyteller;public bool IsCustomStoryteller;}
public static class TranslationManager {
 public static Dictionary<string,string> Text=new Dictionary<string,string>();
 public static string Get(string key)=>Text.TryGetValue(key,out string value)?value:key;
}
public static class MeasurementUnitService {public static string FormatSurfaceArea(float area)=>area+" square metres";}
namespace ParalivesAccess {
 public static class Loc {public static string Get(string key,params object[] args)=>key=="lot_detail_separator"?". ":key+":"+string.Join("|",args);}
 public class LogAdapter {public void LogInfo(object value){}public void LogError(object value){throw new Exception(value.ToString());}}
 public static class Plugin {public static LogAdapter Log=new LogAdapter();}
}
namespace ParalivesAccess.Speech {
 public static class ScreenReader {
  public static List<string> Sent=new List<string>();public static void Speak(string text){Sent.Add(text);}
  public static string NormalizeForOutput(string text)=>text==null?null:System.Text.RegularExpressions.Regex.Replace(text,"<[^>]+>","").Trim();
 }
}
namespace ParalivesAccess.Handlers {
 public static class LotActivityReader {public static int Reads;public static string Text;public static string Describe(AssetLot lot){Reads++;return Text;}}
 public static partial class WorldInformationChecks {
  static int count,assetReads,snapshots;static ulong lastLotGuid;static AssetLot availableAsset;
  static void Check(bool condition,string label){if(!condition)throw new Exception(label);count++;}
  static AssetLot GetLot(ulong guid){assetReads++;lastLotGuid=guid;return availableAsset;}
  static LotPerimeterZoneObject GetPerimeter(UILotInfo info)=>info.Perimeter;
  static string ReadText(TextAdapter label)=>label!=null&&label.gameObject.activeInHierarchy?label.text:null;
  static string ReadActiveTranslatedText(TranslatedText label)=>ReadText(label);
  static void AddUnique(List<string> parts,string text){if(!string.IsNullOrWhiteSpace(text)&&!parts.Contains(text.Trim()))parts.Add(text.Trim());}
  class Snapshot {public static Snapshot Create(AssetLot lot){snapshots++;return new Snapshot();}public string DescribeStructure()=>"2 bedrooms";public string DescribeFacilities()=>"Cooking available";}
  public static string Run(){
   Check(RelationshipLocationHandler.ApplyPatches(new HarmonyLib.Harmony()),"native Locate hook resolves");
   var zones=ZoneManager.Instance;
   var currentLot=new LotPerimeterZoneObject{DisplayName="12 River Street",Description="<b>A quiet riverside home.</b>",RealLotGUID=1,SurfaceArea=100};
   var destination=new LotPerimeterZoneObject{DisplayName="Town Cafe",RealLotGUID=2};
   zones.Lots[1]=currentLot;zones.Lots[2]=destination;zones.Positions[10]=currentLot;
   var para=new AssetCharacter();para.Data.FullName="Alex";para.Data.Position=new UnityEngine.Vector3(10);para.Data.CurrentTownAutonomyTargetLot=2;para.Data.TownAutonomyDestinationState=Setting.TownAutonomyDestinationState.HeadingToALot;
   Check(RelationshipLocationHandler.Describe(para)=="relationship_location:Alex|12 River Street","travelling Para is located by position, not intended destination");
   para.Data.Position=new UnityEngine.Vector3(20);
   Check(RelationshipLocationHandler.Describe(para)=="relationship_location_unknown:Alex","street or unavailable position does not fall back to intended destination");
   para.IsInImpostorLot=true;para.Data.TownAutonomyDestinationState=Setting.TownAutonomyDestinationState.Idle;
   Check(RelationshipLocationHandler.Describe(para)=="relationship_location:Alex|Town Cafe","arrived impostor occupant uses its occupied lot");
   para.Data.TownAutonomyDestinationState=Setting.TownAutonomyDestinationState.HeadingToALot;para.Data.Position=new UnityEngine.Vector3(10);
   Check(RelationshipLocationHandler.Describe(para)=="relationship_location:Alex|12 River Street","impostor flag alone cannot report a destination before arrival");
   para.Data.TownAutonomyDestinationState=Setting.TownAutonomyDestinationState.OutOfTown;
   Check(RelationshipLocationHandler.Describe(para)=="relationship_location_out_of_town:Alex","out-of-town status does not report a stale map position");
   Check(RelationshipLocationHandler.Describe(null)=="relationship_location_unknown:state_unknown_para:","missing character is reported without guessing");
   var icon=new UITownMapLotIcon{LotGUID=1};
   string brief=DescribeTownMapLot(icon,"Residential : 50000");
   Check(brief=="Residential : 50000"&&snapshots==0&&assetReads==0&&LotActivityReader.Reads==0,"regular lot entry uses only the concise native label without identity or activity reads");
   string details=DescribeTownMapLot(icon,"Residential : 50000",true);
   Check(details=="12 River Street. Residential : 50000. A quiet riverside home."&&snapshots==0,"F3 reads name/address, native tooltip and normalized authored description without reading inventory");
   availableAsset=new AssetLot();details=DescribeTownMapLot(icon,"Residential : 50000",true);
   Check(!details.Contains("square metres")&&!details.Contains("bedrooms")&&!details.Contains("Cooking")&&snapshots==0,"a loaded asset does not add area, structural counts or facilities to F3");
   currentLot.Description="New description";Check(DescribeTownMapLot(icon,"Residential",true).Contains("New description"),"lot descriptions are read live on each request");
   icon.LotGUID=2;Check(!DescribeTownMapLot(icon,"Cafe",true).Contains("New description"),"pooled icon changes do not reuse the previous lot description");
   Check(DescribeTownMapLot(icon,"Cafe",true)=="Town Cafe. Cafe","empty authored descriptions add no filler");
   destination.DisplayName="<b>Castle Plaza</b>";
   Check(DescribeTownMapLot(icon,"Castle Plaza",true)=="Castle Plaza","normalized native identity and tooltip are not duplicated");
   icon.LotGUID=99;
   Check(DescribeTownMapLot(icon,"Beach",true)=="Beach","missing perimeter retains native tooltip without inventing an address");
   var lotInfo=new UILotInfo{Perimeter=currentLot,LotGUID=1,LotTypeLabel=new TextAdapter{text="Residential"},LotSizeLabel=new TextAdapter{text="100 square metres"},ForSale=new UnityEngine.GameObject(),BuyButtonText=new TranslatedText{text="Buy for 50000"}};
   availableAsset=null;
   string infoText=DescribeLotInfo(lotInfo,false);
   Check(infoText.Contains("12 River Street")&&infoText.Contains("New description")&&infoText.Contains("Buy for 50000")&&!infoText.Contains("square metres")&&snapshots==0,"Lot Info F3 retains authored and visible purchase text without an inventory load");
   availableAsset=new AssetLot();infoText=DescribeLotInfo(lotInfo);
   Check(infoText.Contains("100 square metres")&&infoText.Contains("2 bedrooms")&&infoText.Contains("Cooking available")&&snapshots==1,"ordinary Lot Info summary retains its existing readiness information");
   lotInfo.ForSale.activeInHierarchy=false;lotInfo.BuyButtonText.gameObject.activeInHierarchy=false;
   lotInfo.LotHouseholdContainer=new UnityEngine.GameObject();lotInfo.LotHouseholdNameLabel=new TextAdapter{text="The Chen Household"};
   lotInfo.Perimeter=destination;infoText=DescribeLotInfo(lotInfo,false);
   Check(infoText.Contains("Castle Plaza")&&infoText.Contains("The Chen Household")&&!infoText.Contains("Buy for")&&!infoText.Contains("12 River Street"),"Lot Info F3 rereads perimeter and visible ownership without stale purchase text");
   Check(DescribeLotInfo(null,false)==null,"missing Lot Info window does not produce stale content");
   destination.DisplayName="";destination.NativeAddress="402 Castle Plaza";destination.RealLotGUID=55;zones.Lots[55]=destination;icon.LotGUID=55;
   availableAsset.Data.Companies=new ulong[]{11,11,12,99};
   Settings.Occupations.Companies[11]=new Setting.Company{DisplayName="Restaurant2Italian"};
   Settings.Occupations.Companies[12]=new Setting.Company{DisplayName="UntranslatedCompany"};
   TranslationManager.Text["CompanyName_Restaurant2Italian"]="<b>La Tavola di Nonna</b>";
   details=DescribeTownMapLot(icon,"Restaurant",true);
   Check(details=="402 Castle Plaza. La Tavola di Nonna. Restaurant","native street address and linked translated business name survive empty DisplayName/Description, with duplicates and missing translations omitted");
   Check(lastLotGuid==55&&snapshots==1,"company metadata uses the effective lot without scanning inventory");
   Check(DescribeTownMapLot(icon,"Restaurant")=="Restaurant","address and company name are confined to F3");
   destination.NativeAddress="418 Castle Plaza";details=DescribeTownMapLot(icon,"Restaurant",true);
   Check(details.Contains("418 Castle Plaza")&&!details.Contains("402 Castle Plaza"),"address is read from the current perimeter, not cached or substituted from template metadata");
   TranslationManager.Text["CompanyName_Restaurant2Italian"]="Changed business";
   Check(DescribeTownMapLot(icon,"Restaurant",true).Contains("Changed business"),"company translations are read live");
   lotInfo.LotGUID=55;infoText=DescribeLotInfo(lotInfo,false);
   Check(infoText.Contains("418 Castle Plaza")&&infoText.Contains("Changed business")&&snapshots==1,"native Lot Info F3 shares the corrected identity reader");
   availableAsset.Data.Companies=null;destination.NativeAddress="";
   Check(DescribeTownMapLot(icon,"Shop",true)=="Shop","absent native address and company data never produce an invented identity");
   LotActivityReader.Text="Native activities";
   Check(DescribeTownMapLot(icon,"Shop",true).Contains("Native activities")&&!DescribeTownMapLot(icon,"Shop").Contains("Native activities"),"native activity preview is confined to explicit F3");
   int reads=LotActivityReader.Reads;
   Check(!DescribeLotInfo(lotInfo).Contains("Native activities")&&LotActivityReader.Reads==reads,"automatic Lot Info summary does not read the activity preview");
   Check(DescribeLotInfo(lotInfo,false).Contains("Native activities"),"Lot Info F3 includes the native activity preview");
   Settings.LotTypes.Types[1]=new Setting.LotType{GUID=1,DisplayName="Museum"};Settings.LotTypes.Types[2]=new Setting.LotType{GUID=2,DisplayName="Shop"};
   TranslationManager.Text["LotType_Museum"]="Museum";TranslationManager.Text["LotType_Shop"]="Shop";
   LotManager.Instance.Lots[70]=new AssetLot{Data=new AssetLotData{LotTypeGUID=1}};LotManager.Instance.Lots[71]=new AssetLot{Data=new AssetLotData{LotTypeGUID=2}};
   zones.Lots[70]=new LotPerimeterZoneObject{NativeAddress="99 Castle Plaza"};zones.Lots[71]=new LotPerimeterZoneObject{NativeAddress="1 Castle Plaza"};
   var museum=new LotEntry(new UITownMapLotIcon{LotGUID=70,ToolTip=new TooltipOpenerSimple{TextToShow="Museum"}});
   var shop=new LotEntry(new UITownMapLotIcon{LotGUID=71,ToolTip=new TooltipOpenerSimple{TextToShow="Shop"}});
   Check(CompareLotNames(museum,shop)<0&&museum.GetShortLabel()=="Museum"&&shop.GetShortLabel()=="Shop","production sort/classification and command headings use original lot descriptions, never house numbers");
   var item=new UIStorytellerSelectionItem{Storyteller=new Setting.Storyteller{DisplayName="Calm"}};
   TranslationManager.Text["Storyteller_Calm"]="Calm storyteller";TranslationManager.Text["StorytellerDescription_Calm"]="<b>Gentler challenges.</b>";
   Check(Story(item)=="tutorial_storyteller_choice:Calm storyteller|Gentler challenges.","storyteller choice includes its native description without markup");
   Check(Story(new UnityEngine.Component{parent=item})==Story(item),"portrait child focus uses the same storyteller description");
   TranslationManager.Text.Remove("StorytellerDescription_Calm");Check(Story(item)=="Calm storyteller","missing description does not speak a translation key");
   item.IsCustomStoryteller=true;TranslationManager.Text["UIStoryteller_CustomStoryteller"]="Custom storyteller";
   Check(Story(item)=="Custom storyteller","custom choice does not borrow a standard storyteller's description");
   return count+" world-information checks passed; native presentation and speech remain pending.";
  }
 }
}
'@
$entry = 'private class LotEntry { private UITownMapLotIcon _icon; public LotEntry(UITownMapLotIcon icon){_icon=icon;ReadClassification();} internal string LotTypeName,SortName;internal ulong LotTypeGuid;internal bool HasHousehold,CanBeVisited,IsForSale;' + $classification + $shortLabel + '}'
$methods = "`nnamespace ParalivesAccess.Handlers { public static partial class WorldInformationChecks {`n" + $lotMethod + "`n" + $lotInfoMethod + "`n" + $identityMethod + "`n" + $storyMethod + "`n" + $entry + "`n" + $compareLots + "`n} }"
Add-Type -TypeDefinition ($location + "`n" + $adapters + $methods) -Language CSharp
[ParalivesAccess.Handlers.WorldInformationChecks]::Run()
