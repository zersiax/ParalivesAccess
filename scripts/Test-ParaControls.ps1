$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
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
$lot = (Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/LotObjectHandler.cs')).Replace("`r", '')
$methods = @(
    'private static bool IsSelectablePara(',
    'private static bool IsCurrentHouseholdPara(',
    'private static bool CanChangeParaSelection(AssetCharacter',
    'private static bool CanToggleParaSelection(AssetCharacter',
    'private static string DescribeToggleParaSelection(AssetCharacter',
    "private static void ToggleParaSelection(`n            AssetCharacter",
    'private static AssetCharacter GetParaRetainedWhenClearing(',
    'private static void ClearSelectedGroup(',
    'private static void HandleEnter(',
    'private interface IParaSelectionEntry'
) | ForEach-Object { Read-Method $lot $_ }
$creator = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/CreatorHouseholdHandler.cs')
$creator = $creator.Substring($creator.IndexOf('namespace ParalivesAccess.Handlers'))
$panel = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/VirtualPanel.cs')
$panel = $panel.Substring($panel.IndexOf('namespace ParalivesAccess.Handlers'))
# Compile production eligibility, native dispatch, Enter transaction, creator handler and virtual
# panel against explicit adapters. These checks do not claim to execute Unity callbacks or speech.
$adapters = @'
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using ParalivesAccess.Speech;
namespace UnityEngine {
 public class Component {
  public GameObject gameObject; public Transform transform=>gameObject.transform;
  public T GetComponentInParent<T>() where T:Component {for(var t=transform;t!=null;t=t.parent)if(t.gameObject.component is T c)return c;return null;}
 }
 public class GameObject {
  public bool activeInHierarchy=true; public Transform transform;public Component component;
  public GameObject(Transform parent=null){transform=new Transform{parent=parent,gameObject=this};}
  public void SetActive(bool value){activeInHierarchy=value;}
 }
 public class Transform:Component {public Transform parent;public bool IsChildOf(Transform ancestor){for(var p=this;p!=null;p=p.parent)if(p==ancestor)return true;return false;}}
 public struct Vector2 {public float x,y;public Vector2(float a,float b){x=a;y=b;}}
 public static class Mathf {public static float Abs(float n)=>Math.Abs(n);}
}
namespace UnityEngine.InputSystem {
 public class KeyControl {public bool wasPressedThisFrame,wasReleasedThisFrame,isPressed;}
 public class Keyboard {public KeyControl enterKey=new KeyControl(),numpadEnterKey=new KeyControl(),leftShiftKey=new KeyControl(),rightShiftKey=new KeyControl();}
}
namespace HarmonyLib {
 public class HarmonyMethod {public HarmonyMethod(Type t,string n){}}
 public class Harmony {public void Patch(MethodInfo m,HarmonyMethod prefix=null,HarmonyMethod postfix=null){} public IEnumerable<MethodBase> GetPatchedMethods()=>new List<MethodBase>();}
 public static class AccessTools {public static MethodInfo DeclaredMethod(Type t,string n)=>t.GetMethod(n);}
}
public enum GameStates {LiveMode,CharacterCreator,BuildMode}
public class AssetCharacterData {public bool IsUnselectable,IsDead;public string FullName;}
public class AssetCharacter {public ulong GUID;public AssetCharacterData Data=new AssetCharacterData();public bool IsPartOfTutorialHousehold;}
public class AssetHouseholdData {public List<ulong> Characters=new List<ulong>{1,2};}
public class AssetHousehold {public AssetHouseholdData Data=new AssetHouseholdData();}
public class HouseholdManager {public static HouseholdManager Instance=new HouseholdManager();public AssetHousehold CurrentHousehold=new AssetHousehold();}
public class AssetManager {public static AssetManager Instance=new AssetManager();public Dictionary<ulong,AssetCharacter> characters=new Dictionary<ulong,AssetCharacter>();public AssetCharacter GetCharacter(ulong guid)=>characters.TryGetValue(guid,out var c)?c:null;}
public class Player {public GameStates State=GameStates.LiveMode;public int PlayerIndex;public long LimitedCharacterCreatorAccess;public bool IsInIntro,IsInSelectHouseholdTutorial,IsInSelectStoryteller,SelectionAllowed=true;public List<ulong> SelectedCharactersGUID=new List<ulong>{1};public bool CanChangeCharacterSelection()=>SelectionAllowed;}
public class HybridPlayer {public Player Player=new Player();}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();public HybridPlayer GetHybridPlayer(int i)=>HybridPlayer1;}
public class CharacterManager {
 public static CharacterManager Instance=new CharacterManager();public string LastCall;public bool Refuse;
 public int GetNumberOfCharactersInHousehold()=>HouseholdManager.Instance.CurrentHousehold.Data.Characters.Count;
 public void SelectCharacter(ulong g,int p){LastCall="select";if(!Refuse)PlayerManager.Instance.HybridPlayer1.Player.SelectedCharactersGUID=new List<ulong>{g};}
 public void AddSelectedCharacter(ulong g,int p){LastCall="add";var s=PlayerManager.Instance.HybridPlayer1.Player.SelectedCharactersGUID;if(!Refuse&&!s.Contains(g))s.Add(g);}
 public void UnselectCharacter(ulong g,int p){LastCall="remove";if(!Refuse)PlayerManager.Instance.HybridPlayer1.Player.SelectedCharactersGUID.Remove(g);}
}
public class Focusable:Component {public bool Skipped;}
public class FocusableGroup:Component {}
public class UIListItemBase:Focusable {}
public class UIList {public List<UIListItemBase> CurrentItems=new List<UIListItemBase>();}
public class UICharacters:FocusableGroup {public bool IsVisible=true,IsInTargetCharacterSelectionMode;public int PlayerOwnerIndex;public UIList List=new UIList();public GameObject AddCharacterButton;}
public class UICharacterItem:UIListItemBase {public AssetCharacter Character;public ulong CharacterGUID;public int PlayerIndex;public GameObject DeleteButton;public void UpdateItem(){}public void OnHoverEnd(){}public void DeleteButtonClick(){}}
public class UICreateCharacterFromGenetics {public bool IsVisible;}
public class UI {public static UI Instance=new UI();public static UICreateCharacterFromGenetics Genetics=new UICreateCharacterFromGenetics();public static T GetOrNull<T>(int i) where T:class=>Genetics as T;}
namespace ParalivesAccess {
 public static class Loc {public static string Get(string key,params object[] values)=>key+":"+string.Join(",",values);}
 public static class Plugin {public static LogAdapter Log=new LogAdapter();}
 public class LogAdapter {public void LogInfo(string s){}public void LogError(string s){}}
}
namespace ParalivesAccess.Speech {public static class ScreenReader {public static string Last;public static void Speak(string s){Last=s;}public static string NormalizeForOutput(string s)=>s?.Trim();}}
namespace ParalivesAccess.Handlers {
 public static class FocusNavigator {public static bool ShouldSkip(Focusable f)=>f.Skipped;}
 public static class TutorialHandler {public static bool IsMultiselectStep;}
 public static class SelectionChecks {
  static VirtualPanel _panel;static bool _enterPressed,_enterWithShift;static VirtualPanel.IEntry _enterEntry;
  static Player GetPlayer()=>PlayerManager.Instance.HybridPlayer1.Player;
  static void Speak(string s)=>ScreenReader.Speak(s);
  static int count;static void Check(bool b,string label){if(!b)throw new Exception(label);count++;}
  static AssetCharacter Para(ulong g){var c=new AssetCharacter{GUID=g};c.Data.FullName="Para "+g;AssetManager.Instance.characters[g]=c;return c;}
  class Entry:IParaSelectionEntry {public bool IsAvailable{get;set;}=true;public int Toggles,Clicks;public string Describe()=>"para";public void Activate(){Clicks++;}public void ToggleSelection(){Toggles++;}}
  class ObjectEntry:VirtualPanel.IEntry {public bool IsAvailable=>true;public int Clicks;public string Describe()=>"object";public void Activate(){Clicks++;}}
  public static string Run(){
   var a=Para(1);var b=Para(2);var townie=Para(3);var p=GetPlayer();
   Check(CanToggleParaSelection(townie),"ordinary townie can be added outside the tutorial");
   ToggleParaSelection(townie,"Townie","test");Check(p.SelectedCharactersGUID.Contains(3)&&CharacterManager.Instance.LastCall=="add","townie command calls native AddSelectedCharacter");
   Check(DescribeToggleParaSelection(townie).StartsWith("explorer_remove_from_selection"),"command changes to Remove when selected");
   ToggleParaSelection(townie,"Townie","test");Check(!p.SelectedCharactersGUID.Contains(3)&&CharacterManager.Instance.LastCall=="remove","townie can be removed with only one household member selected");
   Check(!CanToggleParaSelection(a),"last selected household member cannot be removed individually");
   p.SelectedCharactersGUID.Add(2);Check(CanToggleParaSelection(a),"one of two selected household members can be removed");
   p.IsInIntro=true;Check(!CanToggleParaSelection(townie),"ordinary intro townies remain excluded");
   TutorialHandler.IsMultiselectStep=true;townie.IsPartOfTutorialHousehold=true;Check(CanToggleParaSelection(townie),"conductor stays eligible in multiselect");
   p.SelectionAllowed=false;Check(!CanToggleParaSelection(townie),"native selection lock is respected");p.SelectionAllowed=true;
   p.IsInSelectHouseholdTutorial=true;Check(!CanToggleParaSelection(a),"household-choice intro owns activation");p.IsInSelectHouseholdTutorial=false;
   p.IsInSelectStoryteller=true;Check(!CanToggleParaSelection(townie),"storyteller selection owns activation");p.IsInSelectStoryteller=false;
   p.IsInIntro=false;TutorialHandler.IsMultiselectStep=false;
   townie.Data.IsUnselectable=true;Check(!CanToggleParaSelection(townie),"unselectable townies are excluded");townie.Data.IsUnselectable=false;
   townie.Data.IsDead=true;Check(!CanToggleParaSelection(townie),"dead Paras are excluded");townie.Data.IsDead=false;
   p.State=GameStates.BuildMode;Check(!CanToggleParaSelection(a),"selection commands do not run in build mode");p.State=GameStates.LiveMode;
   CharacterManager.Instance.Refuse=true;ToggleParaSelection(townie,"Townie","test");Check(ScreenReader.Last.StartsWith("explorer_action_unavailable"),"native refusal is not announced as success");CharacterManager.Instance.Refuse=false;
   p.SelectedCharactersGUID=new List<ulong>{3,2,1};ClearSelectedGroup();Check(p.SelectedCharactersGUID.Count==1&&p.SelectedCharactersGUID[0]==2,"mixed group clears to first selected household Para");
   p.SelectedCharactersGUID=new List<ulong>{3};ClearSelectedGroup();Check(p.SelectedCharactersGUID.Count==1&&p.SelectedCharactersGUID[0]==1,"NPC-only selection returns to a household actor");
   p.SelectedCharactersGUID=new List<ulong>{2};ClearSelectedGroup();Check(p.SelectedCharactersGUID.Count==1&&p.SelectedCharactersGUID[0]==2,"clearing an already solo Para is stable");
   p.SelectedCharactersGUID=new List<ulong>{1,2,3};p.SelectionAllowed=false;ClearSelectedGroup();Check(p.SelectedCharactersGUID.Count==3,"clear respects native selection lock");p.SelectionAllowed=true;
   var e=new Entry();var e2=new Entry();_panel=new VirtualPanel("test");_panel.Add(e);_panel.Add(e2);_panel.EnterSilently();
   var k=new Keyboard();k.enterKey.wasPressedThisFrame=true;k.leftShiftKey.isPressed=true;HandleEnter(k);Check(e.Toggles==0,"Shift Enter waits for release");
   k.enterKey.wasPressedThisFrame=false;k.leftShiftKey.isPressed=false;k.enterKey.wasReleasedThisFrame=true;HandleEnter(k);Check(e.Toggles==1&&e.Clicks==0,"Shift captured on press survives modifier-first release");
   HandleEnter(k);Check(e.Toggles==1,"orphaned release cannot toggle twice");
   k=new Keyboard();k.numpadEnterKey.wasPressedThisFrame=true;k.numpadEnterKey.wasReleasedThisFrame=true;k.rightShiftKey.isPressed=true;HandleEnter(k);Check(e.Toggles==2&&e.Clicks==0,"right Shift and numpad Enter support same-frame taps");
   k=new Keyboard();k.enterKey.wasPressedThisFrame=true;k.enterKey.wasReleasedThisFrame=true;HandleEnter(k);Check(e.Clicks==1&&e.Toggles==2,"plain Enter keeps ordinary activation");
   k=new Keyboard();k.enterKey.wasPressedThisFrame=true;k.leftShiftKey.isPressed=true;HandleEnter(k);_panel.Move(1);k.enterKey.wasPressedThisFrame=false;k.enterKey.wasReleasedThisFrame=true;HandleEnter(k);Check(e2.Toggles==0&&e.Toggles==2,"moving focus mid-gesture cannot select a different Para");
   _panel.EnterSilently(e);k=new Keyboard();k.enterKey.wasPressedThisFrame=true;HandleEnter(k);e.IsAvailable=false;k.enterKey.wasPressedThisFrame=false;k.enterKey.wasReleasedThisFrame=true;HandleEnter(k);Check(e.Clicks==1,"vanished target cannot activate on release");
   var obj=new ObjectEntry();_panel=new VirtualPanel("object");_panel.Add(obj);_panel.EnterSilently();k=new Keyboard();k.enterKey.wasPressedThisFrame=true;k.enterKey.wasReleasedThisFrame=true;k.leftShiftKey.isPressed=true;HandleEnter(k);Check(obj.Clicks==0&&ScreenReader.Last.StartsWith("explorer_selection_shortcut_requires_para"),"Shift Enter on an object does not accidentally open its action");
   return count+" Para-selection checks passed.";
  }
'@
$creatorChecks = @'
 }
 public static class CreatorChecks {
  static int count;static void Check(bool b,string label){if(!b)throw new Exception(label);count++;}
  static T Make<T>(Transform parent=null) where T:Component,new(){var g=new GameObject(parent);var c=new T{gameObject=g};g.component=c;return c;}
  static Focusable Move(UICharacters bar,List<Focusable> all,Focusable source,int x,int y){Focusable next;if(!CreatorHouseholdHandler.TryNavigate(bar,all,source,new Vector2(x,y),out next))throw new Exception("creator household move not handled");return next;}
  static void Refresh(UICharacterItem item)=>typeof(CreatorHouseholdHandler).GetMethod("RefreshDeleteButton",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{item});
  public static string Run(){
   var p=PlayerManager.Instance.HybridPlayer1.Player;p.State=GameStates.CharacterCreator;p.SelectedCharactersGUID=new List<ulong>{2};
   var bar=Make<UICharacters>();var a=Make<UICharacterItem>(bar.transform);var b=Make<UICharacterItem>(bar.transform);
   a.Character=AssetManager.Instance.GetCharacter(1);a.CharacterGUID=1;b.Character=AssetManager.Instance.GetCharacter(2);b.CharacterGUID=2;
   var da=Make<Focusable>(a.transform);a.DeleteButton=da.gameObject;var db=Make<Focusable>(b.transform);b.DeleteButton=db.gameObject;
   var add=Make<Focusable>(bar.transform);bar.AddCharacterButton=add.gameObject;bar.List.CurrentItems.Add(a);bar.List.CurrentItems.Add(b);
   var all=new List<Focusable>{db,b,add,da,a,a};
   Check(CreatorHouseholdHandler.CanDelete(a),"full creator with multiple Paras permits deletion");
   a.DeleteButton.SetActive(false);Refresh(a);Check(a.DeleteButton.activeInHierarchy,"Delete becomes visible without pointer hover");
   Check(CreatorHouseholdHandler.GetDeleteLabel(da).Contains("Para 1"),"Delete label names its own Para");
   Check(CreatorHouseholdHandler.GetDeleteLabel(a)==null,"portrait is not mislabeled as Delete");
   Check(CreatorHouseholdHandler.GetEntry(bar,all)==b,"household entry prefers selected portrait over X controls");
   Check(Move(bar,all,a,1,0)==b&&Move(bar,all,b,-1,0)==a,"portrait navigation uses native list order despite reversed duplicate registration");
   Check(Move(bar,all,a,0,-1)==da&&Move(bar,all,da,0,1)==a,"Down reaches own Delete and Up reverses it");
   Check(Move(bar,all,da,1,0)==b,"Right from Delete goes to the next portrait");
   Check(Move(bar,all,b,1,0)==add&&Move(bar,all,add,-1,0)==b,"Add Para remains the next household action");
   Check(Move(bar,all,a,-1,0)==a&&Move(bar,all,add,1,0)==add,"household boundaries do not wrap");
   p.LimitedCharacterCreatorAccess=1;Refresh(a);Check(!CreatorHouseholdHandler.CanDelete(a)&&!a.DeleteButton.activeInHierarchy,"limited creator hides deletion");p.LimitedCharacterCreatorAccess=0;
   UI.Genetics.IsVisible=true;Check(!CreatorHouseholdHandler.CanDelete(a),"genetics creation owns its pending Para");UI.Genetics.IsVisible=false;
   HouseholdManager.Instance.CurrentHousehold.Data.Characters.Remove(2);Check(!CreatorHouseholdHandler.CanDelete(a),"last household Para cannot be deleted");HouseholdManager.Instance.CurrentHousehold.Data.Characters.Add(2);
   bar.List.CurrentItems.Remove(a);Check(!CreatorHouseholdHandler.CanDelete(a),"pooled portrait cannot delete stale character");bar.List.CurrentItems.Insert(0,a);
   a.CharacterGUID=2;Check(!CreatorHouseholdHandler.CanDelete(a),"mid-refresh GUID mismatch cannot delete wrong Para");a.CharacterGUID=1;
   p.State=GameStates.LiveMode;Refresh(a);Check(!a.DeleteButton.activeInHierarchy,"creator Delete is hidden after returning to live mode");
   Focusable next;Check(!CreatorHouseholdHandler.TryNavigate(bar,all,a,new Vector2(1,0),out next),"live household retains ordinary navigation");
   return count+" creator-household checks passed; native callbacks, focus and speech need an in-game retest.";
  }
 }
}
'@
Add-Type -TypeDefinition ($adapters + "`n" + ($methods -join "`n") + "`n" + $creatorChecks + "`n" + $creator + "`n" + $panel) -Language CSharp
[ParalivesAccess.Handlers.SelectionChecks]::Run()
[ParalivesAccess.Handlers.CreatorChecks]::Run()
