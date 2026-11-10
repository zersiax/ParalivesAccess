
// Boundary adapters: no native menu, Unity lifecycle or speech-delivery claims.
public static class Settings {
 public static Setting.Interactions Interactions;public static Setting.BuildModeCatalogTags Tags;
 public static Setting.Skins Skins;public static Setting.LotTypes Lots;
 public static T Get<T>() where T:class {
  if(typeof(T)==typeof(Setting.Interactions))return Interactions as T;
  if(typeof(T)==typeof(Setting.BuildModeCatalogTags))return Tags as T;
  if(typeof(T)==typeof(Setting.Skins))return Skins as T;return Lots as T;
 }
}
public class UlongAndGuid {public ulong Value;public UlongAndGuid(ulong value){Value=value;}}
public class ItemObjectRoot {public ulong CurrentSkinGUID,IsImpostorForLot;public Setting.InteractionGroup NativeGroup;}
public class InteractionManager {public static InteractionManager Instance=new InteractionManager();public int Calls;public Setting.InteractionGroup GetItemInteractionGroup(ItemObjectRoot r){Calls++;return r.NativeGroup;}}
public class AssetLot {public LotData Data=new LotData();}
public class LotData {public ulong LotTypeGUID;}
public class LotManager {public static LotManager Instance=new LotManager();public AssetLot Lot;public AssetLot GetLotByGUID(ulong guid)=>guid==9?Lot:null;}
public static class TranslationManager {public static readonly System.Collections.Generic.Dictionary<string,string> Text=new System.Collections.Generic.Dictionary<string,string>();public static string Get(string k)=>Text.ContainsKey(k)?Text[k]:k;}
namespace Setting {
 public class CatalogueItem {public bool OverrideInteractionGroup;public ulong Interaction;public UlongAndGuid[] Tag;public string Name,Product;}
 public class InteractionGroup {public ulong GUID,SkinTagGUID;public bool DisplayNameIsSkinnable,DisplayNameIsImpostorLotType;public string TranslationKey;}
 public class Interactions {public readonly System.Collections.Generic.Dictionary<ulong,InteractionGroup> Groups=new System.Collections.Generic.Dictionary<ulong,InteractionGroup>();public InteractionGroup GetInteractionGroupByGUID(ulong g)=>Groups.ContainsKey(g)?Groups[g]:null;}
 public class BuildModeCatalogTag {public ulong InteractionGroup;}
 public class BuildModeCatalogTags {
  public readonly System.Collections.Generic.Dictionary<ulong,BuildModeCatalogTag> Tags=new System.Collections.Generic.Dictionary<ulong,BuildModeCatalogTag>();
  public readonly System.Collections.Generic.Dictionary<ulong,ulong[]> Parents=new System.Collections.Generic.Dictionary<ulong,ulong[]>();
  public BuildModeCatalogTag GetTagByGUID(ulong g)=>Tags.ContainsKey(g)?Tags[g]:null;
  public ulong[] GetParentTags(ulong g)=>Parents.ContainsKey(g)?Parents[g]:new ulong[0];
 }
 public class SkinProperty {public string StringValue;}
 public class Skin {public SkinProperty Property;public SkinProperty GetProperty(ulong g)=>g==7?Property:null;}
 public class Skins {public Skin Skin;public Skin GetSkinByGUID(ulong g)=>g==5?Skin:null;}
 public class LotType {public string TranslationKey;}
 public class LotTypes {public LotType LotType;public LotType GetLotTypeByGUID(ulong g)=>g==6?LotType:null;}
}
namespace ParalivesAccess.Speech {public static class ScreenReader {public static string NormalizeForOutput(string value)=>value?.Replace("<b>","").Replace("</b>","");}}
namespace ParalivesAccess.Handlers {
 public static class Plugin {public static Logger Log=new Logger();}
 public class Logger {public void LogError(string value){throw new System.Exception(value);}}
 public static class Loc {public static string Get(string key,params object[] values)=>string.Join(", ",values);}
 public static class IdentityComposer {
  static string GetName(Setting.CatalogueItem i,ulong s)=>i?.Name;
  static string GetAuthoredProductName(Setting.CatalogueItem i)=>i?.Product;
  IDENTITY_COMPOSER
  public static string Compose(Setting.CatalogueItem i,string heading)=>BuildProductIdentity(i,0,heading);
 }
}
public static class ItemIdentityChecks {
 static int count;
 static void Check(bool ok,string name){count++;if(!ok)throw new System.Exception(name);}
 public static void Run(){
  Settings.Interactions=new Setting.Interactions();Settings.Tags=new Setting.BuildModeCatalogTags();Settings.Skins=new Setting.Skins();Settings.Lots=new Setting.LotTypes();
  var treadmill=new Setting.InteractionGroup{GUID=1,TranslationKey="group.treadmill"};var chair=new Setting.InteractionGroup{GUID=2,TranslationKey="group.chair"};
  Settings.Interactions.Groups[1]=treadmill;Settings.Interactions.Groups[2]=chair;
  TranslationManager.Text["group.treadmill"]="Treadmill";TranslationManager.Text["group.chair"]="Chair";
  var item=new Setting.CatalogueItem{OverrideInteractionGroup=true,Interaction=1,Name="Fite",Product="Phoenix's Burn"};
  Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(item,0)=="Treadmill","override heading before instantiation");
  Check(ParalivesAccess.Handlers.IdentityComposer.Compose(item,"Treadmill")=="Phoenix's Burn, Fite, Treadmill","retain product/collection and add function");
  item.Product=null;Check(ParalivesAccess.Handlers.IdentityComposer.Compose(item,"Treadmill")=="Fite, Treadmill","unnamed product gains function");
  item.Name=null;Check(ParalivesAccess.Handlers.IdentityComposer.Compose(item,"Treadmill")=="Treadmill","heading usable without other names");
  Check(ParalivesAccess.Handlers.IdentityComposer.Compose(null,null)==null,"unknown stays unknown");
  item.Name="Treadmill";Check(ParalivesAccess.Handlers.IdentityComposer.Compose(item,"treadmill")=="Treadmill","case insensitive collection deduplication");
  item.Name="Fite";item.Product="Treadmill";Check(ParalivesAccess.Handlers.IdentityComposer.Compose(item,"treadmill")=="Treadmill, Fite","product deduplication");
  item.Product="Fite";Check(ParalivesAccess.Handlers.IdentityComposer.Compose(item,null)=="Fite","original duplicate suppression");
  Check(ParalivesAccess.Handlers.IdentityComposer.Compose(item,"Treadmill")=="Fite, Treadmill","single collection with function");
  item.Product="Treadmill Deluxe";Check(ParalivesAccess.Handlers.IdentityComposer.Compose(item,"Treadmill")=="Treadmill Deluxe, Fite, Treadmill","substring is not duplicate identity");
  Settings.Tags.Tags[10]=new Setting.BuildModeCatalogTag{InteractionGroup=2};item.Tag=new[]{new UlongAndGuid(10)};
  item.Interaction=999;Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(item,0)==null,"missing explicit override must not turn into chair");
  item.OverrideInteractionGroup=false;Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(item,0)=="Chair","tag based heading");
  Settings.Tags.Tags[11]=new Setting.BuildModeCatalogTag();Settings.Tags.Parents[11]=new ulong[]{10};item.Tag=new[]{new UlongAndGuid(11)};
  Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(item,0)=="Chair","inherited heading");
  Settings.Tags.Tags[12]=new Setting.BuildModeCatalogTag{InteractionGroup=1};item.Tag=new[]{new UlongAndGuid(11),new UlongAndGuid(12)};
  Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(item,0)=="Chair","first tag ancestry wins over later direct tag");
  item.Tag=new[]{new UlongAndGuid(12),new UlongAndGuid(11)};Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(item,0)=="Treadmill","tag order preserved");
  Settings.Tags.Parents[99]=new ulong[]{10};item.Tag=new[]{new UlongAndGuid(99)};Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(item,0)==null,"missing tag ancestry skipped");
  item.Tag=new UlongAndGuid[]{null,new UlongAndGuid(12)};Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(item,0)=="Treadmill","null tag skipped");
  item.Tag=null;Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(item,0)==null,"no tag no guessed function");
  var root=new ItemObjectRoot{NativeGroup=treadmill};
  Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(root)=="Treadmill"&&InteractionManager.Instance.Calls==1,"placed/spawned object uses native resolver");
  root.NativeGroup=chair;Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(root)=="Chair","no stale group cache");
  root.NativeGroup=treadmill;treadmill.DisplayNameIsSkinnable=true;treadmill.SkinTagGUID=7;root.CurrentSkinGUID=5;
  Settings.Skins.Skin=new Setting.Skin{Property=new Setting.SkinProperty{StringValue="skin.recipe"}};TranslationManager.Text["skin.recipe"]="<b>Apple pie</b>";
  Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(root)=="Apple pie","localized skin heading and output normalization");
  Settings.Skins.Skin.Property=null;Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(root)=="Treadmill","missing skin property falls back");
  Settings.Skins.Skin.Property=new Setting.SkinProperty{StringValue="missing.skin"};Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(root)=="Treadmill","untranslated skin key never spoken");
  treadmill.DisplayNameIsSkinnable=false;treadmill.DisplayNameIsImpostorLotType=true;root.IsImpostorForLot=9;
  LotManager.Instance.Lot=new AssetLot();LotManager.Instance.Lot.Data.LotTypeGUID=6;Settings.Lots.LotType=new Setting.LotType{TranslationKey="lot.gym"};TranslationManager.Text["lot.gym"]="Gym";
  Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(root)=="Gym","impostor heading uses actual lot type");
  root.IsImpostorForLot=0;Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(root)=="Treadmill","missing lot falls back");
  treadmill.TranslationKey="missing.group";Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(root)==null,"untranslated group key never spoken");
  TranslationManager.Text["missing.group"]="  ";Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(root)==null,"blank translation omitted");
  Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName((ItemObjectRoot)null)==null,"null live root");
  Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName((Setting.CatalogueItem)null,0)==null,"null catalogue row");
  Settings.Interactions=null;Check(ParalivesAccess.Handlers.ItemInteractionNameReader.GetName(item,0)==null,"unavailable settings");
  System.Console.WriteLine("Passed "+count+" item identity boundary checks. Native menu and speech delivery require the player scenario.");
 }
}
