$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$handler = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\ContextMenuSearchHandler.cs')
# Compile the production popup handler. These adapters model native selection and live list
# membership; Unity's deferred text activation and speech still require the in-game check.
$adapters = @'
namespace UnityEngine {
 public class Component {
  public GameObject gameObject;
  public T GetComponentInParent<T>() where T:Component {
   for(var go=gameObject;go!=null;go=go.parent){var item=go.GetComponent<T>();if(item!=null)return item;}return null;
  }
 }
 public class GameObject {
  public GameObject parent;public bool activeInHierarchy=true;List<Component> components=new List<Component>();
  public GameObject(GameObject p=null){parent=p;}
  public T AddComponent<T>() where T:Component,new(){var item=new T{gameObject=this};components.Add(item);return item;}
  public T GetComponent<T>() where T:Component {foreach(var item in components)if(item is T found)return found;return null;}
 }
 public struct Vector2 {public float x,y;public Vector2(float a,float b){x=a;y=b;}}
}
namespace UnityEngine.EventSystems {
 public class EventSystem {
  public static EventSystem current=new EventSystem();public UnityEngine.GameObject currentSelectedGameObject;
  public void SetSelectedGameObject(UnityEngine.GameObject go){currentSelectedGameObject=go;}
 }
}
namespace TMPro {
 public class TMP_InputField:UnityEngine.Component {
  public string text="";public int caretPosition,activations;
  public void Select(){UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(gameObject);}
  public void ActivateInputField(){activations++;}
 }
}
public class Focusable:UnityEngine.Component {public bool skip;public string label;}
public class FocusableGroup:UnityEngine.Component {}
public class UIWindow:FocusableGroup {public bool IsVisible=true;public Focusable focused;public Focusable GetFocused(bool recursive)=>focused;}
public class UIListItemBase:Focusable {}
public class UIContextMenuWidgetItem:UIListItemBase {}
public class UIList {public List<UIListItemBase> CurrentItems=new List<UIListItemBase>();}
public class UIContextMenuWidget:UIWindow {public bool ShowSearchBox=true;public TMPro.TMP_InputField InputFieldSearch;public UIList ItemList=new UIList();}
namespace ParalivesAccess {
 public static class Loc {public static string Get(string key,params object[] args)=>key+":"+string.Join("|",args);}
 public class LogAdapter {public void LogInfo(string message){}}
 public static class Plugin {public static LogAdapter Log=new LogAdapter();}
}
namespace ParalivesAccess.Speech {
 public static class ScreenReader {public static List<string> Sent=new List<string>();public static void Speak(string message){Sent.Add(message);}}
}
namespace ParalivesAccess.Handlers {
 public static class FocusNavigator {public static bool ShouldSkip(Focusable item)=>item.skip;}
 public static class MenuNavigationHandler {public static Focusable Cursor;public static FocusableGroup CursorGroup;}
 public static class UiLabelReader {public static string GetLabelOrFallback(Focusable item)=>item.label;}
 public static class DropdownSearchChecks {
  static int count;
  static void Check(bool condition,string name){if(!condition)throw new System.Exception(name);count++;}
  public static string Run(){
   var window=new UnityEngine.GameObject().AddComponent<UIContextMenuWidget>();
   var input=new UnityEngine.GameObject(window.gameObject).AddComponent<TMPro.TMP_InputField>();window.InputFieldSearch=input;
   var first=new UnityEngine.GameObject(window.gameObject).AddComponent<UIContextMenuWidgetItem>();
   var last=new UnityEngine.GameObject(window.gameObject).AddComponent<UIContextMenuWidgetItem>();
   var stale=new UnityEngine.GameObject(window.gameObject).AddComponent<UIContextMenuWidgetItem>();stale.gameObject.activeInHierarchy=false;
   window.ItemList.CurrentItems.Add(stale);window.ItemList.CurrentItems.Add(first);window.ItemList.CurrentItems.Add(last);
   var down=new UnityEngine.Vector2(0,-1);var up=new UnityEngine.Vector2(0,1);var left=new UnityEngine.Vector2(-1,0);
   var events=UnityEngine.EventSystems.EventSystem.current;Focusable result;
   input.Select();
   Check(ContextMenuSearchHandler.GetLabel(input)=="dropdown_search:","empty native search has a useful label and keyboard instructions");
   Check(!ContextMenuSearchHandler.TryLeaveSearch(window,left,out result)&&events.currentSelectedGameObject==input.gameObject,"caret arrows remain in the native search field");
   Check(ContextMenuSearchHandler.TryLeaveSearch(window,down,out result)&&result==first,"Down enters the first live result without skipping it");
   Check(events.currentSelectedGameObject==null,"leaving search clears native selection so text-entry suppression can end");
   Check(!ContextMenuSearchHandler.TryLeaveSearch(window,down,out result),"ordinary result navigation is left to the existing navigator");
   Check(!ContextMenuSearchHandler.TryReturnToSearch(window,last,up),"Up within the results does not prematurely enter search");
   Check(ContextMenuSearchHandler.TryReturnToSearch(window,first,up)&&events.currentSelectedGameObject==input.gameObject&&input.activations==1,"Up from the first option selects and activates the real search field");
   input.text="English";
   Check(ContextMenuSearchHandler.GetLabel(input)=="dropdown_search_value:English","search label reads the current native query");
   first.gameObject.activeInHierarchy=false;
   Check(ContextMenuSearchHandler.TryLeaveSearch(window,down,out result)&&result==last&&input.text=="English","filtered list uses current active results and preserves the native query");
   input.Select();last.gameObject.activeInHierarchy=false;
   Check(ContextMenuSearchHandler.TryLeaveSearch(window,down,out result)&&result==null&&events.currentSelectedGameObject==input.gameObject,"empty search keeps its editor available");
   Check(ScreenReader.Sent[ScreenReader.Sent.Count-1]=="dropdown_no_results:","empty result navigation explains why it stayed in search");
   first.gameObject.activeInHierarchy=true;last.gameObject.activeInHierarchy=true;
   Check(ContextMenuSearchHandler.TryLeaveSearch(window,up,out result)&&result==last,"Up from search enters the final live option");
   var unrelated=new UnityEngine.GameObject().AddComponent<TMPro.TMP_InputField>();unrelated.Select();
   Check(!ContextMenuSearchHandler.TryLeaveSearch(window,down,out result)&&events.currentSelectedGameObject==unrelated.gameObject,"unrelated numeric and name editors keep ownership");
   Check(ContextMenuSearchHandler.GetLabel(unrelated)==null,"unrelated text fields retain their existing labels");
   input.Select();window.ShowSearchBox=false;
   Check(!ContextMenuSearchHandler.TryLeaveSearch(window,down,out result)&&!ContextMenuSearchHandler.TryReturnToSearch(window,first,up),"resolution-style dropdowns without search keep existing navigation");
   window.ShowSearchBox=true;window.IsVisible=false;
   Check(!ContextMenuSearchHandler.TryLeaveSearch(window,down,out result)&&ContextMenuSearchHandler.GetLabel(input)==null,"hidden dropdowns cannot take input or provide stale search labels");
   window.IsVisible=true;input.gameObject.activeInHierarchy=false;
   Check(!ContextMenuSearchHandler.TryLeaveSearch(window,down,out result),"inactive native search cannot take input");

   // Reproduce the household's one-option More options popup: foreground seeding is silent,
   // and arrows cannot reach another row to trigger focus speech.
   window.ShowSearchBox=false;events.SetSelectedGameObject(null);
   window.ItemList.CurrentItems.Clear();window.ItemList.CurrentItems.Add(first);
   first.label="Play without a lot";last.label="Another option";
   MenuNavigationHandler.CursorGroup=window;MenuNavigationHandler.Cursor=first;
   string one="panel_with_focus:window_opened_one:Options|Play without a lot";
   int sentBefore=ScreenReader.Sent.Count;
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",1)==one,"one-option popup names the silently focused Play without a lot action");
   Check(ScreenReader.Sent.Count==sentBefore&&MenuNavigationHandler.Cursor==first&&events.currentSelectedGameObject==null,"describing the popup does not speak separately or change selection");
   first.label="Updated action";
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",1)=="panel_with_focus:window_opened_one:Options|Updated action","reused popup reads the current label instead of caching the previous action");
   first.label="Play without a lot";
   var otherWindow=new UnityEngine.GameObject().AddComponent<UIContextMenuWidget>();
   var otherRow=new UnityEngine.GameObject(otherWindow.gameObject).AddComponent<UIContextMenuWidgetItem>();otherRow.label="Unrelated action";
   MenuNavigationHandler.CursorGroup=otherWindow;MenuNavigationHandler.Cursor=otherRow;window.focused=first;
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",1)==one,"another panel's mod cursor uses this popup's native focus");
   MenuNavigationHandler.CursorGroup=window;MenuNavigationHandler.Cursor=null;
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",1)==one,"native focus is read when no mod cursor exists");
   MenuNavigationHandler.Cursor=otherRow;
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",1)=="window_opened_one:Options","a cursor belonging to another popup cannot leak its label");
   MenuNavigationHandler.Cursor=first;window.ItemList.CurrentItems.Clear();window.ItemList.CurrentItems.Add(last);
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",1)=="window_opened_one:Options","pooled active rows outside CurrentItems are excluded");
   window.ItemList.CurrentItems.Add(first);first.gameObject.activeInHierarchy=false;
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",1)=="window_opened_one:Options","hidden selected rows cannot provide stale labels");
   first.gameObject.activeInHierarchy=true;first.skip=true;
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",1)=="window_opened_one:Options","non-navigable selected rows cannot provide labels");
   first.skip=false;MenuNavigationHandler.Cursor=last;
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",2)=="panel_with_focus:window_opened:Options|2|Another option","multiple-option popup names the actual cursor rather than always its first row");
   input.gameObject.activeInHierarchy=true;window.ShowSearchBox=true;input.text="";input.Select();
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",2)=="panel_with_focus:window_opened:Options|2|dropdown_search:","native search ownership takes precedence over silently seeded result focus");
   input.text="English";
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",2)=="panel_with_focus:window_opened:Options|2|dropdown_search_value:English","search opening reads the current query");
   unrelated.Select();
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",2)=="panel_with_focus:window_opened:Options|2|Another option","an unrelated selected editor does not masquerade as popup search");
   window.IsVisible=false;
   Check(ContextMenuSearchHandler.DescribeOpening(window,"Options",2)==null&&ContextMenuSearchHandler.DescribeOpening(null,"Options",0)==null,"hidden or missing popups are not announced");
   return count+" dropdown/popup checks passed; native rendering and speech remain pending.";
  }
 }
}
'@
Add-Type -TypeDefinition ($handler + "`n" + $adapters) -Language CSharp
[ParalivesAccess.Handlers.DropdownSearchChecks]::Run()

# The combined announcement must be composed after silent foreground seeding, before the generic
# title/count return. This guards the actual window lifecycle wiring as well as the helper above.
$windowHandler = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\WindowHandler.cs')
$settled = $windowHandler.Substring($windowHandler.IndexOf('private static string DescribeSettledWindow('))
$seed = $settled.IndexOf('MenuNavigationHandler.FocusForegroundWindow(window);')
$popup = $settled.IndexOf('ContextMenuSearchHandler.DescribeOpening(contextMenu, name, count);')
$generic = $settled.IndexOf('if (count > 0 && !HasSemanticWindowSummary(window))')
if ($seed -lt 0 -or $popup -le $seed -or $generic -le $popup) { throw 'Popup opening announcement is not wired after focus seeding and before generic window speech.' }
Write-Host 'Popup announcement lifecycle wiring check passed.'
