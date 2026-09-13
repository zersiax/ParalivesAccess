$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\LogicalFocusNavigator.cs')
$navigation = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\MenuNavigationHandler.cs')
$commitStart = $navigation.IndexOf('        internal static void CommitArrowFocus(')
$commitEnd = $navigation.IndexOf('        private static Focusable FindSemanticSeed(', $commitStart)
if ($commitStart -lt 0 -or $commitEnd -lt 0) { throw 'Missing production commit method.' }
$commit = $navigation.Substring($commitStart, $commitEnd - $commitStart)
# Compile the production ordering and focus commit against native-list/hierarchy adapters.
# Coordinates are deliberately unrelated to logical order; SetFocused checks cursor publication.
$adapters = @'
namespace UnityEngine {
 public class Component {
  public GameObject gameObject; public Transform transform=>gameObject.transform;
  public T GetComponent<T>() where T:class=>gameObject.GetComponent<T>();
  public T GetComponentInParent<T>() where T:class {for(var n=transform;n!=null;n=n.parent){var c=n.GetComponent<T>();if(c!=null)return c;}return null;}
 }
 public class GameObject {
  public Transform transform;public bool activeSelf=true;public bool activeInHierarchy=>activeSelf&&(transform.parent==null||transform.parent.gameObject.activeInHierarchy);
  public List<Component> Components=new List<Component>();
  public GameObject(Transform parent=null){transform=new Transform{gameObject=this};Components.Add(transform);transform.parent=parent;if(parent!=null)parent.Children.Add(transform);}
  public T Add<T>() where T:Component,new(){var c=new T{gameObject=this};Components.Add(c);return c;}
  public T GetComponent<T>() where T:class {foreach(var c in Components)if(c is T result)return result;return null;}
 }
 public class Transform:Component {public Transform parent;public Vector2 position;public List<Transform> Children=new List<Transform>();public int GetSiblingIndex()=>parent==null?0:parent.Children.IndexOf(this);}
 public struct Vector2 {
  public float x,y;public Vector2(float a,float b){x=a;y=b;}public static Vector2 zero=>new Vector2(0,0);
  public static bool operator ==(Vector2 a,Vector2 b)=>a.x==b.x&&a.y==b.y;public static bool operator !=(Vector2 a,Vector2 b)=>!(a==b);
  public override bool Equals(object o)=>o is Vector2 v&&this==v;public override int GetHashCode()=>x.GetHashCode()^y.GetHashCode();
 }
}
public class Focusable:UnityEngine.Component {public bool IsFirstFocused,IsShoulderButton,Skip;public string Label;}
public class FocusableGroup:UnityEngine.Component {
 public Focusable Focused;
 public void SetFocused(Focusable target){
  if(!ReferenceEquals(ParalivesAccess.Handlers.MenuNavigationHandler.Cursor,target)||!ReferenceEquals(ParalivesAccess.Handlers.MenuNavigationHandler.CursorGroup,this))throw new Exception("Cursor must be published before the focus callback");
  Focused=target;ParalivesAccess.Speech.ScreenReader.SpeakIfChanged(target.Label);
 }
}
public class ParaToggle:Focusable {public bool IsOn;}
public class UICharacterCreator:FocusableGroup {}
public class UICharacterCreatorContextualList:FocusableGroup {}
public class UICharacterSubMenuBar:FocusableGroup {}
public class UIRelationships:FocusableGroup {public UIList UIListOtherCharacters;}
public class UIList:UnityEngine.Component {public List<UIListItemBase> CurrentItems=new List<UIListItemBase>();}
public class UIListItemBase:Focusable {public UIList ParentUIList;public bool IsOn;}
namespace ParalivesAccess.Speech {
 public static class ScreenReader {public static string Last;public static int Calls,Resets;
  public static void ResetLastSpoken(){Last=null;Resets++;}
  public static void SpeakIfChanged(string text){if(text!=Last){Last=text;Calls++;}}
 }
}
namespace ParalivesAccess.Handlers {
 public static class FocusNavigator {
  public static int SpatialCalls;
  public static bool ShouldSkip(Focusable f)=>f.Skip;
  public static Focusable FindTarget(FocusableGroup g,List<Focusable> controls,Focusable source,Vector2 direction){SpatialCalls++;return controls[0];}
  public static List<Focusable> GetDistinctReachable(List<Focusable> controls){SpatialCalls++;return controls;}
  public static int CountDistinctReachable(List<Focusable> controls){SpatialCalls++;return controls.Count;}
 }
 public static class MenuNavigationHandler {
  private static Focusable _cursor;private static FocusableGroup _cursorGroup;
  public static Focusable Cursor=>_cursor;public static FocusableGroup CursorGroup=>_cursorGroup;
  COMMIT_METHOD
 }
 public static class LogicalFocusChecks {
  static int checks;
  static Vector2 Down=new Vector2(0,-1),Up=new Vector2(0,1),Left=new Vector2(-1,0),Right=new Vector2(1,0);
  static void Check(bool pass,string label){if(!pass)throw new Exception(label);checks++;}
  static T Make<T>(Transform parent=null) where T:Component,new()=>new GameObject(parent).Add<T>();
  static UIListItemBase Row(UIList list,Transform parent,string label){var row=Make<UIListItemBase>(parent);row.ParentUIList=list;row.Label=label;list.CurrentItems.Add(row);return row;}
  static bool Ordered(List<Focusable> actual,params Focusable[] expected){if(actual.Count!=expected.Length)return false;for(int i=0;i<expected.Length;i++)if(actual[i]!=expected[i])return false;return true;}
  public static string Run(){
   var group=Make<FocusableGroup>();var a=Make<Focusable>(group.transform);var b=Make<Focusable>(group.transform);var c=Make<Focusable>(group.transform);
   a.transform.position=new Vector2(500,-200);b.transform.position=new Vector2(-500,200);c.transform.position=new Vector2(0,0);
   var registered=new List<Focusable>{c,b,a};
   Check(Ordered(LogicalFocusNavigator.GetOrdered(registered),a,b,c),"control hierarchy wins over screen coordinates and registration timing");
   Check(LogicalFocusNavigator.FindTarget(group,registered,a,Down)==b,"Down moves to the next logical control");
   Check(LogicalFocusNavigator.FindTarget(group,registered,b,Right)==c,"Right moves forward in ordinary panels");
   Check(LogicalFocusNavigator.FindTarget(group,registered,c,Up)==b&&LogicalFocusNavigator.FindTarget(group,registered,b,Left)==a,"Up and Left reverse the same route");
   Check(LogicalFocusNavigator.FindTarget(group,registered,a,Up)==null&&LogicalFocusNavigator.FindTarget(group,registered,c,Down)==null,"outer edges never wrap to unrelated controls");
   a.transform.position=b.transform.position=c.transform.position=Vector2.zero;
   Check(Ordered(LogicalFocusNavigator.GetOrdered(registered),a,b,c),"coincident or unsettled visuals do not merge distinct controls");
   registered.Add(a);Check(LogicalFocusNavigator.CountReachable(group,registered)==3,"only duplicate registrations of the same control are merged");
   b.gameObject.activeSelf=false;Check(Ordered(LogicalFocusNavigator.GetOrdered(registered),a,c),"hidden controls are excluded");b.gameObject.activeSelf=true;
   b.Skip=true;Check(Ordered(LogicalFocusNavigator.GetOrdered(registered),a,c),"existing semantic skip rules remain authoritative");b.Skip=false;
   b.IsShoulderButton=true;Check(Ordered(LogicalFocusNavigator.GetOrdered(registered),a,c)&&LogicalFocusNavigator.CountReachable(group,registered)==3,"shoulder controls retain their separate navigation track and count");b.IsShoulderButton=false;
   Check(LogicalFocusNavigator.FindTarget(group,registered,null,Down)==a,"missing cursor seeds the first logical control");
   b.IsFirstFocused=true;Check(LogicalFocusNavigator.FindTarget(group,registered,null,Down)==b,"native default focus hint is honored when reseeding");b.IsFirstFocused=false;
   Check(LogicalFocusNavigator.FindTarget(group,registered,a,Vector2.zero)==null,"neutral input does not move focus");

   var relations=Make<UIRelationships>();var portraits=Make<UIList>(relations.transform);relations.UIListOtherCharacters=portraits;
   var roberto=Row(portraits,portraits.transform,"Roberto");var florence=Row(portraits,portraits.transform,"Florence");var bianca=Row(portraits,portraits.transform,"Bianca");
   var profile=Make<UIList>(relations.transform);
   // Reproduce replacement ordering: Personality was created first, Identity last,
   // while the native CurrentItems list still starts with Identity.
   var personality=Row(profile,profile.transform,"Personality");var unknownPersonality=Row(profile,profile.transform,"Unknown Information");
   var occupation=Row(profile,profile.transform,"Occupations");var unknownOccupation=Row(profile,profile.transform,"Unknown Information");
   var identity=Row(profile,profile.transform,"Identity");var unknownIdentity=Row(profile,profile.transform,"Unknown Information");
   profile.CurrentItems=new List<UIListItemBase>{identity,unknownIdentity,occupation,unknownOccupation,personality,unknownPersonality};
   var rows=new List<Focusable>{personality,unknownPersonality,bianca,occupation,unknownOccupation,florence,identity,unknownIdentity,roberto};
   Check(Ordered(LogicalFocusNavigator.GetOrdered(rows),roberto,florence,bianca,identity,unknownIdentity,occupation,unknownOccupation,personality,unknownPersonality),"portraits stay together and profile follows native CurrentItems order");
   Check(LogicalFocusNavigator.FindTarget(relations,rows,bianca,Down)==identity,"leaving the portrait list enters the beginning of the next section");
   Check(LogicalFocusNavigator.FindTarget(relations,rows,identity,Up)==bianca,"reverse traversal returns to the adjacent section boundary");
   profile.CurrentItems.Remove(unknownIdentity);
   Check(!LogicalFocusNavigator.GetOrdered(rows).Contains(unknownIdentity),"active stale pooled rows outside CurrentItems remain unreachable");
   var staleChild=Make<Focusable>(unknownIdentity.transform);rows.Add(staleChild);
   Check(!LogicalFocusNavigator.GetOrdered(rows).Contains(staleChild),"children of stale pooled rows remain unreachable");
   profile.CurrentItems.Insert(1,unknownIdentity);rows.Remove(staleChild);
   var child=Make<Focusable>(identity.transform);rows.Add(child);
   var reordered=LogicalFocusNavigator.GetOrdered(rows);
   Check(reordered.IndexOf(child)==reordered.IndexOf(identity)+1,"an item's own controls stay with their item");
   roberto.IsOn=true;Check(LogicalFocusNavigator.GetEntry(relations,rows)==roberto,"Relationships entry uses the selected native portrait");
   roberto.IsOn=false;florence.IsOn=true;Check(LogicalFocusNavigator.GetEntry(relations,rows)==florence,"entry selection is read live");
   florence.gameObject.activeSelf=false;Check(LogicalFocusNavigator.GetEntry(relations,rows)==roberto,"hidden selected portrait cannot become the entry");florence.gameObject.activeSelf=true;
   Check(LogicalFocusNavigator.CountReachable(relations,rows)==LogicalFocusNavigator.GetOrdered(rows).Count,"spoken counts agree with reachable controls after pooling");

   var bar=Make<UICharacterSubMenuBar>();var thoughts=Make<ParaToggle>(bar.transform);var skills=Make<ParaToggle>(bar.transform);var social=Make<ParaToggle>(bar.transform);social.IsOn=true;
   var tabs=new List<Focusable>{social,thoughts,skills};
   Check(LogicalFocusNavigator.GetEntry(bar,tabs)==social,"tab entry uses the game's currently open panel");
   Check(LogicalFocusNavigator.FindTarget(bar,tabs,thoughts,Down)==skills&&LogicalFocusNavigator.FindTarget(bar,tabs,skills,Down)==social,"tabs follow one stable logical order");
   skills.gameObject.activeSelf=false;Check(LogicalFocusNavigator.FindTarget(bar,tabs,thoughts,Right)==social,"hidden tutorial or disabled-feature tabs are skipped");
   Check(LogicalFocusNavigator.GetEntry(group,registered)==null,"unrelated screen landing rules are preserved");

   var creator=Make<UICharacterCreator>();var creatorPanel=Make<FocusableGroup>(creator.transform);
   int calls=FocusNavigator.SpatialCalls;LogicalFocusNavigator.FindTarget(creatorPanel,registered,a,Right);
   Check(FocusNavigator.SpatialCalls==calls+1,"creator's dedicated/spatial fallback is preserved");
   var contextual=Make<UICharacterCreatorContextualList>();Check(LogicalFocusNavigator.UsesSpatialNavigation(contextual),"separate creator contextual windows retain their fallback");
   calls=FocusNavigator.SpatialCalls;LogicalFocusNavigator.CountReachable(creatorPanel,registered);LogicalFocusNavigator.GetReachable(creatorPanel,registered);
   Check(FocusNavigator.SpatialCalls==calls+2,"creator counts and diagnostic lists retain existing duplicate handling");

   a.Label=b.Label="Unknown Information";
   MenuNavigationHandler.CommitArrowFocus(group,a);int speech=ScreenReader.Calls;
   MenuNavigationHandler.CommitArrowFocus(group,b);
   Check(ScreenReader.Calls==speech+1&&group.Focused==b,"moving to a different identically named control speaks and commits that exact target");
   speech=ScreenReader.Calls;int resets=ScreenReader.Resets;MenuNavigationHandler.CommitArrowFocus(group,b);
   Check(ScreenReader.Calls==speech&&ScreenReader.Resets==resets,"repeated assignment to the same control does not bypass speech deduplication");
   Check(LogicalFocusNavigator.GetOrdered(null).Count==0&&LogicalFocusNavigator.CountReachable(group,null)==0,"missing lists have no stale focus targets");
   return checks+" logical-focus checks passed; native focus, scrolling and speech remain runtime checks.";
  }
 }
}
'@
$adapters = $adapters.Replace('COMMIT_METHOD', $commit)
Add-Type -TypeDefinition ("using ParalivesAccess.Speech;`n" + $source + "`n" + $adapters) -Language CSharp
[ParalivesAccess.Handlers.LogicalFocusChecks]::Run()
