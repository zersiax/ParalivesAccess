$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
function Read-Source([string]$name) {
    Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot "src/Handlers/$name.cs")
}
function Read-Block([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature)
    if ($start -lt 0) { throw "Missing production block: $signature" }
    $body = $source.IndexOf('{', $start); $depth = 1; $end = $body + 1
    while ($depth -gt 0) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    $source.Substring($start, $end - $start)
}
$sources = @('ItemAttentionReader','TransitStopReader','ParaSwitchHandler') | ForEach-Object {
    (Read-Source $_) -replace '(?m)^using [^;]+;\r?\n', ''
}
$identity = Read-Block (Read-Source 'WorldTargeting') 'internal static bool IsSameLiveItem('
$destination = Read-Block (Read-Source 'TownMapLotHandler') 'private sealed class FastTravelEntry'
$destination = $destination.Replace('private sealed class FastTravelEntry', 'public sealed class FastTravelEntry')

# Compile production readers, shortcut handler, identity guard and destination entry against
# explicit game/UI adapters. These tests verify mod decisions and native delegation, not Unity.
$adapters = @'
namespace UnityEngine {
 public class Object {}
 public class GameObject {public bool activeInHierarchy=true;}
}
namespace UnityEngine.InputSystem {
 public enum Key {F9}
 public class KeyControl {public bool isPressed;}
 public class Keyboard {
  public static Keyboard current=new Keyboard();
  public KeyControl ctrlKey=new KeyControl(),altKey=new KeyControl(),shiftKey=new KeyControl();
 }
}
namespace Setting {
 public class UlongAndGuid {public ulong Value;}
 public class ItemStates {public ulong ItemStateOnFire=1,ItemStateBroken=2,ItemStateBurnt=3,ItemStateDirty=4,ItemStateSpoiledFood=5;}
 public class ItemBrokenness {public ulong WaterPuddleItemGUID=80;}
 public class Fire {public ulong FireItemGUID=90;}
 public class FastTravelMethod {public bool NeedsMapSelectionBasedOnItem=true;public UlongAndGuid[] CatalogueItems;}
 public class FastTravelMethods {public FastTravelMethod[] AllFastTravelMethods;}
 public class CatalogueItem {public bool OverrideInteractionGroup;public ulong Interaction;}
 public class Items {public Dictionary<ulong,CatalogueItem> Values=new Dictionary<ulong,CatalogueItem>();public CatalogueItem GetItemByGUID(ulong id)=>Values.TryGetValue(id,out var value)?value:null;}
 public class InteractionGroup {public ulong GUID;}
}
public static class Settings {
 public static Dictionary<Type,object> Values=new Dictionary<Type,object>();
 public static T Get<T>() where T:class=>Values.TryGetValue(typeof(T),out var value)?value as T:null;
}
public class ItemObjectText:UnityEngine.Object {public string Text;}
public class ItemObjectRoot {
 public UnityEngine.GameObject gameObject=new UnityEngine.GameObject();
 public int InstanceID;public ulong CatalogueGUID,LotPlacedOnGUID;public string Name="Object";
 public bool CanSetAlphaDirty,IsOnFire;
 public Dictionary<ulong,float> StateValues=new Dictionary<ulong,float>();
 public List<UnityEngine.Object> Children=new List<UnityEngine.Object>();
 public InteractionGroup Group;public List<UnityEngine.Object> GetChildren<T>()=>Children;
}
public class ItemManager {
 public static ItemManager Instance=new ItemManager();
 public List<ItemObjectRoot> AllItems=new List<ItemObjectRoot>();
 public ItemObjectRoot GetItemByInstanceID(int id){for(int i=AllItems.Count-1;i>=0;i--){var root=AllItems[i];if(root==null){AllItems.RemoveAt(i);continue;}if(root.InstanceID==id)return root;}return null;}
}
public class InteractionManager {
 public static InteractionManager Instance=new InteractionManager();
 public InteractionGroup GetItemInteractionGroup(ItemObjectRoot root)=>root.Group;
}
public enum State {Game,Menu}
public enum GameStates {LiveMode,CharacterCreator}
public static class GameLoadingManager {public static State State=State.Game;}
public class ItemObjectInPlacement {public ItemObjectRoot Item;}
public class Player {
 public int PlayerIndex;public GameStates State=GameStates.LiveMode;
 public bool IsUsingFreeCamera=true,IsInIntro,PlayerActionBlockedByUIWindow,AllowSelection=true;
 public ItemObjectInPlacement ItemInPlacement;
 public ulong TownMapForFastTravelMethod,CharacterForFastTravel,CameraCurrentCharacterFollowTarget;
 public List<ulong> SelectedCharactersGUID=new List<ulong>();
 public bool CanChangeCharacterSelection()=>AllowSelection;
 public ulong GetSelectedCharacterGUID()=>SelectedCharactersGUID.Count==0?0:SelectedCharactersGUID[0];
}
public class HybridPlayer {public Player Player=new Player();public bool HasInputFieldFocused;}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public class UICharacters {public bool IsInTargetCharacterSelectionMode,IsTogetherCardsMode;}
public static class UI {public static UICharacters Portraits=new UICharacters();public static T GetOrNull<T>(int id) where T:class=>Portraits as T;}
public class AssetCharacterData {public string FullName;public bool IsDeadOrTakenAway;}
public class AssetCharacter {public ulong GUID;public AssetCharacterData Data=new AssetCharacterData();}
public class AssetHouseholdData {public List<ulong> Characters=new List<ulong>();}
public class AssetHousehold {public AssetHouseholdData Data=new AssetHouseholdData();}
public class HouseholdManager {public static HouseholdManager Instance=new HouseholdManager();public AssetHousehold CurrentHousehold=new AssetHousehold();}
public class CharacterManager {
 public static CharacterManager Instance=new CharacterManager();
 public Dictionary<ulong,AssetCharacter> Characters=new Dictionary<ulong,AssetCharacter>();
 public int SelectCalls;
 public AssetCharacter GetCharacterByGUID(ulong id)=>Characters.TryGetValue(id,out var value)?value:null;
 public void SelectCharacter(ulong guid,int playerIndex) {
  SelectCalls++;var player=PlayerManager.Instance.HybridPlayer1.Player;
  if(player.CanChangeCharacterSelection())player.SelectedCharactersGUID=new List<ulong>{guid};
 }
}
public class TextLabel {public string text;}
public class UITownMapFastTravelButton {
 public UnityEngine.GameObject gameObject=new UnityEngine.GameObject();
 public TextLabel FastTravelIDLabel=new TextLabel();public int Clicks;
 public void Clicked(){Clicks++;}
}
public class UITownMap {
 public bool IsVisible=true;public int Cancels;
 public Dictionary<int,UITownMapFastTravelButton> FastTravelIcons=new Dictionary<int,UITownMapFastTravelButton>();
 public void CancelFastTravel(){Cancels++;var p=PlayerManager.Instance.HybridPlayer1.Player;p.TownMapForFastTravelMethod=0;p.CharacterForFastTravel=0;}
}
namespace ParalivesAccess {
 public static class Loc {public static string Get(string key,params object[] args)=>key+":"+string.Join(",",args);}
 public class Logger {public void LogInfo(string s){} public void LogError(string s){throw new Exception(s);}}
 public static class Plugin {public static Logger Log=new Logger();}
}
namespace ParalivesAccess.Speech {
 public static class ScreenReader {public static string Last;public static void Speak(string s){Last=s;}}
}
namespace ParalivesAccess.Handlers {
 public static class ItemDescription {public static string GetProductIdentity(ItemObjectRoot root)=>root?.Name;}
 public static class ModInputKey {
  public static bool Pressed=true,Available=true;
  public static bool WasPressed(Keyboard keyboard,Key key)=>Pressed;
  public static bool IsAvailable(HybridPlayer hybrid,Key key,out string conflict){conflict="conflict";return Available;}
 }
 public static class SettingsHandler {public static bool IsCapturingBindingInput;}
 public static class WindowHandler {public static bool IsSceneTransitionActive;}
 public static class TutorialHandler {public static bool OwnsInput;}
 public static class ContextActionHandler {public static bool OwnsInput;}
 public static class OccupationHandler {public static bool OwnsInput;}
 public static class ItemPlacementHandler {public static bool OwnsInput;}
 public static class LotObjectHandler {public static int Closes;public static void CloseForParaSwitch(){Closes++;}}
 public static class CameraFollowHandler {public static bool FrameFollowStart(Player p,AssetCharacter c,string s)=>p.CameraCurrentCharacterFollowTarget==c.GUID;}
 public static class LotBuildInputOwnership {public static int Holds;public static void HoldThroughCurrentFrame(){Holds++;}}
 public class VirtualPanel {public interface IEntry {bool IsAvailable{get;} string Describe();void Activate();}}
}
'@
$tests = @'
public static class HouseholdToolsTests {
 static int checks;
 static void Check(bool result,string description){checks++;if(!result)throw new Exception(description);}
 static void SwitchBlocked(Action block,Action reset,string label){var p=PlayerManager.Instance.HybridPlayer1.Player;ulong before=p.GetSelectedCharacterGUID();int calls=CharacterManager.Instance.SelectCalls;block();ParaSwitchHandler.Tick();Check(p.GetSelectedCharacterGUID()==before&&CharacterManager.Instance.SelectCalls==calls,label);reset();}
 public static int Run(){
  var states=new ItemStates();Settings.Values[typeof(ItemStates)]=states;Settings.Values[typeof(ItemBrokenness)]=new ItemBrokenness();
  var root=new ItemObjectRoot{CatalogueGUID=50,InstanceID=1};
  Check(ItemAttentionReader.Describe(root)==null,"ordinary item is not a problem");
  root.StateValues[4]=0.99f;Check(ItemAttentionReader.Describe(root)==null,"partial dirtiness is not a completed dirty state");
  root.CanSetAlphaDirty=true;Check(ItemAttentionReader.Describe(root).Contains("attention_dirty"),"native visible partial dirt is exposed");
  root.CanSetAlphaDirty=false;
  root.StateValues[4]=1;Check(ItemAttentionReader.Describe(root).Contains("attention_dirty"),"native dirty state is listed");
  root.StateValues[2]=1;Check(ItemAttentionReader.Describe(root).Contains("attention_broken"),"broken and dirty coexist");
  root.StateValues[4]=0;Check(!ItemAttentionReader.Describe(root).Contains("attention_dirty"),"cleaning is reflected without a rebuild");
  root.StateValues.Clear();root.StateValues[99]=1;Check(ItemAttentionReader.Describe(root)==null,"unrelated on/full states are not guessed");
  root.Name="Dirty puddle decoration";Check(ItemAttentionReader.Describe(root)==null,"names do not imply cleanable spills");
  root.CatalogueGUID=80;Check(ItemAttentionReader.Describe(root).Contains("attention_puddle"),"configured spawned puddle is cleanable");
  ((ItemBrokenness)Settings.Values[typeof(ItemBrokenness)]).WaterPuddleItemGUID=81;Check(ItemAttentionReader.Describe(root)==null,"puddle configuration is read live");
  root.CatalogueGUID=50;
  foreach(var pair in new Dictionary<ulong,string>{{1,"attention_fire"},{3,"attention_burnt"},{5,"attention_spoiled"}}){root.StateValues.Clear();root.StateValues[pair.Key]=1;Check(ItemAttentionReader.Describe(root).Contains(pair.Value),pair.Value);}
  root.StateValues[5]=float.NaN;Check(ItemAttentionReader.Describe(root)==null,"invalid state values are not problems");
  states.ItemStateDirty=0;root.StateValues[0]=1;Check(ItemAttentionReader.Describe(root)==null,"unset state references ignored");
  Settings.Values[typeof(Fire)]=new Fire();root.CatalogueGUID=90;
  Check(ItemAttentionReader.Describe(root).Contains("attention_soot"),"configured fire debris has cleanup");
  root.IsOnFire=true;Check(ItemAttentionReader.Describe(root)==null,"burning fire spot not mislabeled as soot");root.IsOnFire=false;root.CatalogueGUID=50;

  var method=new FastTravelMethod{CatalogueItems=new[]{new UlongAndGuid{Value=70}}};
  Settings.Values[typeof(FastTravelMethods)]=new FastTravelMethods{AllFastTravelMethods=new[]{method}};
  var items=new Items();items.Values[70]=new CatalogueItem{OverrideInteractionGroup=true,Interaction=700};Settings.Values[typeof(Items)]=items;
  var sign=new ItemObjectRoot{InstanceID=2,CatalogueGUID=70,LotPlacedOnGUID=999};
  var shelter=new ItemObjectRoot{InstanceID=3,CatalogueGUID=71,Group=new InteractionGroup{GUID=700}};
  Check(TransitStopReader.IsStop(sign),"native destination catalogue sign included");
  Check(TransitStopReader.IsStop(shelter),"shelter sharing the sign's native group included");
  Check(!TransitStopReader.IsStop(root),"ordinary objects excluded");
  ItemManager.Instance.AllItems.AddRange(new[]{root,sign,shelter});
  var player=PlayerManager.Instance.HybridPlayer1.Player;
  Check(TransitStopReader.GetStops(player).Count==2,"town-owned stops need not belong to current lot");
  ItemManager.Instance.AllItems.Add(null);Check(TransitStopReader.GetStops(player).Count==2,"native lookup may prune destroyed registry entries during discovery");
  sign.gameObject.activeInHierarchy=false;Check(TransitStopReader.GetStops(player).Count==1,"inactive stops excluded");sign.gameObject.activeInHierarchy=true;
  player.ItemInPlacement=new ItemObjectInPlacement{Item=sign};Check(TransitStopReader.GetStops(player).Count==1,"placement previews excluded");player.ItemInPlacement=null;
  player.IsInIntro=true;Check(TransitStopReader.GetStops(player).Count==0,"town stops hidden during intro");player.IsInIntro=false;
  var text=new ItemObjectText{Text="12"};sign.Children.Add(text);Check(TransitStopReader.GetLabel(sign).Contains("12"),"visible stop number spoken");text.Text="13";Check(TransitStopReader.GetLabel(sign).Contains("13"),"stop number reread");
  method.NeedsMapSelectionBasedOnItem=false;Check(!TransitStopReader.IsStop(sign),"non-map fast travel not misclassified");method.NeedsMapSelectionBasedOnItem=true;

  foreach(ulong id in new ulong[]{11,22,33}){CharacterManager.Instance.Characters[id]=new AssetCharacter{GUID=id,Data=new AssetCharacterData{FullName="Para "+id}};HouseholdManager.Instance.CurrentHousehold.Data.Characters.Add(id);}
  player.SelectedCharactersGUID.Add(11);ParaSwitchHandler.Tick();Check(player.GetSelectedCharacterGUID()==22&&player.CameraCurrentCharacterFollowTarget==22,"forward selects and follows");
  Keyboard.current.shiftKey.isPressed=true;ParaSwitchHandler.Tick();Check(player.GetSelectedCharacterGUID()==11,"reverse selects rather than shift-groups");ParaSwitchHandler.Tick();Check(player.GetSelectedCharacterGUID()==33,"reverse wraps");Keyboard.current.shiftKey.isPressed=false;
  player.SelectedCharactersGUID.Add(22);ParaSwitchHandler.Tick();Check(player.GetSelectedCharacterGUID()==11&&player.SelectedCharactersGUID.Count==1,"group replaced by one household Para");
  player.SelectedCharactersGUID=new List<ulong>{999};ParaSwitchHandler.Tick();Check(player.GetSelectedCharacterGUID()==11,"townie selection starts at first household member");
  CharacterManager.Instance.Characters[22].Data.IsDeadOrTakenAway=true;ParaSwitchHandler.Tick();Check(player.GetSelectedCharacterGUID()==33,"unavailable household member skipped");
  Check(LotObjectHandler.Closes==6&&TownMapLotHandler.Closes==6,"custom browsers relinquish ownership on successful switches");
  SwitchBlocked(()=>PlayerManager.Instance.HybridPlayer1.HasInputFieldFocused=true,()=>PlayerManager.Instance.HybridPlayer1.HasInputFieldFocused=false,"typing guard");
  SwitchBlocked(()=>SettingsHandler.IsCapturingBindingInput=true,()=>SettingsHandler.IsCapturingBindingInput=false,"rebinding guard");
  SwitchBlocked(()=>player.PlayerActionBlockedByUIWindow=true,()=>player.PlayerActionBlockedByUIWindow=false,"modal guard");
  SwitchBlocked(()=>player.AllowSelection=false,()=>player.AllowSelection=true,"native selection guard");
  SwitchBlocked(()=>player.IsInIntro=true,()=>player.IsInIntro=false,"intro guard");
  SwitchBlocked(()=>UI.Portraits.IsInTargetCharacterSelectionMode=true,()=>UI.Portraits.IsInTargetCharacterSelectionMode=false,"target picker guard");
  SwitchBlocked(()=>UI.Portraits.IsTogetherCardsMode=true,()=>UI.Portraits.IsTogetherCardsMode=false,"together cards guard");
  SwitchBlocked(()=>player.TownMapForFastTravelMethod=7,()=>player.TownMapForFastTravelMethod=0,"bus destination guard");
  SwitchBlocked(()=>ModInputKey.Available=false,()=>ModInputKey.Available=true,"rebound native hotkey guard");
  SwitchBlocked(()=>Keyboard.current.ctrlKey.isPressed=true,()=>Keyboard.current.ctrlKey.isPressed=false,"control modifier guard");
  SwitchBlocked(()=>WindowHandler.IsSceneTransitionActive=true,()=>WindowHandler.IsSceneTransitionActive=false,"scene transition guard");

  player.TownMapForFastTravelMethod=7;player.CharacterForFastTravel=11;
  var map=new UITownMap();var button=new UITownMapFastTravelButton();button.FastTravelIDLabel.text="13";map.FastTravelIcons[2]=button;
  var destination=new TownMapLotHandler.FastTravelEntry(map,player,2,button,sign);
  Check(destination.IsAvailable&&destination.Describe().Contains("13"),"destination reads actual native button");
  destination.Activate();Check(button.Clicks==1,"destination delegates to native Clicked exactly once");
  player.CharacterForFastTravel=22;Check(!destination.IsAvailable,"stale actor rejected");player.CharacterForFastTravel=11;
  player.TownMapForFastTravelMethod=8;Check(!destination.IsAvailable,"stale method rejected");player.TownMapForFastTravelMethod=7;
  map.FastTravelIcons[2]=new UITownMapFastTravelButton();destination.Activate();Check(button.Clicks==1,"replaced pooled button not invoked");map.FastTravelIcons[2]=button;
  var replacement=new ItemObjectRoot{InstanceID=2,CatalogueGUID=70};ItemManager.Instance.AllItems.Add(replacement);Check(!destination.IsAvailable,"recycled item identity rejected");ItemManager.Instance.AllItems.Remove(replacement);
  map.IsVisible=false;Check(!destination.IsAvailable,"hidden map rejected");map.IsVisible=true;
  button.gameObject.activeInHierarchy=false;Check(!destination.IsAvailable,"hidden destination rejected");button.gameObject.activeInHierarchy=true;
  var cancel=new TownMapLotHandler.FastTravelEntry(map,player,-1,null,null);cancel.Activate();Check(map.Cancels==1&&player.TownMapForFastTravelMethod==0,"cancel delegates to native map and clears travel mode");
  Check(!destination.IsAvailable&&!cancel.IsAvailable,"completed travel invalidates all old entries");
  return checks;
 }
}
'@
$code = "using System; using System.Collections.Generic; using Setting; using UnityEngine.InputSystem; using ParalivesAccess; using ParalivesAccess.Handlers; using ParalivesAccess.Speech;`n" + $adapters + ($sources -join "`n") + @"
namespace ParalivesAccess.Handlers {
 public static class WorldTargeting { $identity }
 public static class TownMapLotHandler {
  public static int Closes;public static void CloseForParaSwitch(){Closes++;}
  private static void ClearPanelOwnership(){}
  $destination
 }
}
"@ + $tests
Add-Type -TypeDefinition $code
$count = [HouseholdToolsTests]::Run()
Write-Host "Household tools: $count checks passed."
