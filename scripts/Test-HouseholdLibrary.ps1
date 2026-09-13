$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$handler = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\HouseholdLibraryHandler.cs')
# Compile the complete production handler. The fixture reproduces the reported eight-card,
# 42-control library with native list order independent of registration and layout coordinates.
$adapters = @'
namespace UnityEngine {
 public class Component {
  public GameObject gameObject; public Transform transform=>gameObject.transform;
  public T GetComponent<T>() where T:Component=>gameObject.GetComponent<T>();
  public T GetComponentInParent<T>() where T:Component {
   for(Transform t=transform;t!=null;t=t.parent){var c=t.gameObject.GetComponent<T>();if(c!=null)return c;}return null;
  }
 }
 public class GameObject {
  public bool activeInHierarchy=true; public Transform transform; private List<Component> components=new List<Component>();
  public GameObject(Transform parent=null,int order=0){transform=new Transform{gameObject=this,parent=parent,index=order};components.Add(transform);}
  public T AddComponent<T>() where T:Component,new(){var c=new T{gameObject=this};components.Add(c);return c;}
  public T GetComponent<T>() where T:Component {foreach(var c in components)if(c is T t)return t;return null;}
 }
 public class Transform:Component {
  public Transform parent;public int index;public int GetSiblingIndex()=>index;
  public bool IsChildOf(Transform ancestor){for(var p=this;p!=null;p=p.parent)if(p==ancestor)return true;return false;}
 }
 public class RectTransform:Transform {public Rect rect;public Vector2 anchoredPosition;public Bounds bounds;}
 public struct Rect {public float yMin,yMax;}
 public struct Bounds {public Vector2 min,max;}
 public struct Vector2 {public float x,y;public Vector2(float a,float b){x=a;y=b;}public float sqrMagnitude=>x*x+y*y;}
 public static class Time {public static float unscaledTime;}
 public static class Mathf {public static float Abs(float v)=>Math.Abs(v);}
 public static class Canvas {public static void ForceUpdateCanvases(){}}
 public static class RectTransformUtility {public static Bounds CalculateRelativeRectTransformBounds(RectTransform v,RectTransform r)=>r.bounds;}
}
namespace UnityEngine.InputSystem {
 public class Delta {public UnityEngine.Vector2 value;public UnityEngine.Vector2 ReadValue()=>value;}
 public class Mouse {public static Mouse current=new Mouse();public Delta delta=new Delta();}
}
namespace UnityEngine.UI {
 public class ScrollRect:UnityEngine.Component {public bool vertical=true;public UnityEngine.RectTransform viewport,content;public int stops;public void StopMovement(){stops++;}}
}
public class Focusable:UnityEngine.Component {public bool IsShoulderButton,Skipped;}
public class FocusableGroup:Focusable {}
public class ParaToggle:Focusable {public bool IsOn;}
public class UIListItemBase:ParaToggle {}
public class UIList:UnityEngine.Component {public List<UIListItemBase> CurrentItems=new List<UIListItemBase>();}
public class UIPremadeHouseholdSelectionItem:UIListItemBase {}
public class SmallCharacterThumbnailListItem:UIListItemBase {}
public class UIPremadeHouseholdSelection:FocusableGroup {
 public bool IsVisible=true;public UIList PremadeHouseholdList;
 public UnityEngine.GameObject HeaderPanel,CloseButton,SelectButton;
 public ParaToggle OfficialContent,OwnContent,WorkshopContent;
 public UnityEngine.UI.ScrollRect ScrollRect;
}
namespace ParalivesAccess {
 public static class Loc {public static string Get(string key)=>key;}
}
namespace ParalivesAccess.Handlers {
 public static class FocusNavigator {public static bool ShouldSkip(Focusable f)=>f.Skipped;}
 public static class LibraryChecks {
  static int count;
  static void Check(bool condition,string label){if(!condition)throw new Exception(label);count++;}
  static T Make<T>(UnityEngine.Transform parent,int order=0) where T:UnityEngine.Component,new()=>new UnityEngine.GameObject(parent,order).AddComponent<T>();
  static Focusable Move(FocusableGroup w,List<Focusable> all,Focusable from,int x,int y) {
   Focusable next; if(!HouseholdLibraryHandler.TryNavigate(w,all,from,new UnityEngine.Vector2(x,y),out next))throw new Exception("library navigation not handled");return next;
  }
  public static string Run(){
   var w=Make<UIPremadeHouseholdSelection>(null);w.HeaderPanel=new UnityEngine.GameObject(w.transform,0);
   w.OfficialContent=Make<ParaToggle>(w.HeaderPanel.transform,0);w.OwnContent=Make<ParaToggle>(w.HeaderPanel.transform,1);w.WorkshopContent=Make<ParaToggle>(w.HeaderPanel.transform,2);
   w.PremadeHouseholdList=Make<UIList>(w.transform,1);
   var browse=Make<Focusable>(w.transform,2);var close=Make<Focusable>(w.transform,3);w.CloseButton=close.gameObject;
   var all=new List<Focusable>{w.OfficialContent,w.OwnContent,w.WorkshopContent,browse,close};
   var rows=new List<UIPremadeHouseholdSelectionItem>();var children=new List<List<Focusable>>();
   int[] sizes={3,2,2,3,2,2,4,3};
   for(int i=0;i<sizes.Length;i++){
    var row=Make<UIPremadeHouseholdSelectionItem>(w.PremadeHouseholdList.transform,i);rows.Add(row);w.PremadeHouseholdList.CurrentItems.Add(row);all.Add(row);
    var members=new UnityEngine.GameObject(row.transform,0);var details=new List<Focusable>();
    for(int j=0;j<sizes[i];j++){var para=Make<SmallCharacterThumbnailListItem>(members.transform,j);details.Add(para);all.Add(para);}
    var badge=Make<Focusable>(row.transform,1);details.Add(badge);all.Add(badge);children.Add(details);
   }
   Check(all.Count==42,"fixture matches the logged eight households and 42 native focusables");
   all.Reverse();all.Add(rows[0]);
   Check(HouseholdLibraryHandler.GetEntry(w,all)==rows[0],"library opens on the first household rather than the top-left portrait");
   Check(Move(w,all,rows[0],0,-1)==rows[1],"Down follows native household order despite reversed and duplicate registration");
   Check(Move(w,all,rows[1],0,1)==rows[0],"Up reverses the household move");
   Check(Move(w,all,rows[0],-1,0)==rows[0],"Left on a household cannot wrap to another card's badge");
   Check(Move(w,all,rows[0],1,0)==children[0][0],"Right explicitly enters this household's first portrait");
   Check(Move(w,all,children[0][0],-1,0)==rows[0],"Left from first portrait returns to its household");
   Check(Move(w,all,children[0][1],1,0)==children[0][2],"Right follows member order within one household");
   Check(Move(w,all,children[0][2],1,0)==children[0][3],"origin badge remains available as a household detail");
   Check(Move(w,all,children[0][3],1,0)==children[0][3],"Right at the final detail cannot jump to Close or another household");
   Check(Move(w,all,children[0][1],0,-1)==rows[1],"Down from a portrait goes to the next household card");
   Check(Move(w,all,children[3][0],0,1)==rows[2],"Up from Lucy's detail goes to the previous household rather than a spatial column");
   Focusable next;string label;
   Check(HouseholdLibraryHandler.TrySwitchSection(w,all,rows[2],1,out next,out label)&&next==browse&&label=="library_actions","apostrophe reaches available actions before a household is selected");
   var play=Make<Focusable>(w.transform,9);w.SelectButton=play.gameObject;all.Add(play);rows[2].IsOn=true;
   HouseholdLibraryHandler.TrySwitchSection(w,all,rows[2],1,out next,out label);
   Check(next==play,"newly revealed Play action leads the actions section");
   HouseholdLibraryHandler.TrySwitchSection(w,all,play,-1,out next,out label);
   Check(next==rows[2]&&label=="library_households","return from actions restores the browsed household");
   Check(HouseholdLibraryHandler.GetEntry(w,all)==rows[2],"reopening prefers the native selected household");
   HouseholdLibraryHandler.TrySwitchSection(w,all,rows[2],-1,out next,out label);
   Check(next==w.OfficialContent&&label=="library_filters","semicolon reaches the first native filter");
   Check(Move(w,all,w.OfficialContent,0,-1)==w.OwnContent,"filters remain operable in hierarchy order");
   Check(Move(w,all,w.OfficialContent,0,1)==w.OfficialContent&&Move(w,all,close,0,-1)==close,"outer boundaries do not wrap unpredictably");
   Check(Move(w,all,rows[7],0,-1)==play,"Down from the final household reaches its revealed actions");
   w.PremadeHouseholdList.CurrentItems.Remove(rows[1]);
   Check(Move(w,all,rows[0],0,-1)==rows[2],"filtered pooled rows are excluded even while stale registration remains");
   Check(Move(w,all,children[1][0],1,0)==rows[2],"a stale child cannot keep navigation in a removed household");
   rows[2].gameObject.activeInHierarchy=false;
   HouseholdLibraryHandler.TrySwitchSection(w,all,play,-1,out next,out label);
   Check(next==rows[0],"hidden remembered and selected household is not restored");
   rows[2].gameObject.activeInHierarchy=true;
   children[0][1].gameObject.activeInHierarchy=false;
   Check(Move(w,all,children[0][0],1,0)==children[0][2],"inactive portraits are excluded after a thumbnail refresh");
   var extra=Make<Focusable>(rows[0].transform,2);all.Add(extra);
   Check(Move(w,all,children[0][3],1,0)==extra,"future native child controls remain reachable");
   w.PremadeHouseholdList.CurrentItems.Clear();
   Check(HouseholdLibraryHandler.GetEntry(w,all)==w.OfficialContent,"empty filter results leave filters reachable");
   w.IsVisible=false;
   Check(!HouseholdLibraryHandler.TryNavigate(w,all,close,new UnityEngine.Vector2(0,-1),out next),"hidden library does not own arrows");
   Check(!HouseholdLibraryHandler.TrySwitchSection(Make<FocusableGroup>(null),all,close,1,out next,out label),"other panels retain their own section behavior");
   w.IsVisible=true;w.ScrollRect=Make<UnityEngine.UI.ScrollRect>(w.transform);
   w.ScrollRect.viewport=Make<UnityEngine.RectTransform>(w.transform);w.ScrollRect.viewport.rect=new UnityEngine.Rect{yMin=0,yMax=100};
   w.ScrollRect.content=Make<UnityEngine.RectTransform>(w.transform);
   var rect=rows[0].gameObject.AddComponent<UnityEngine.RectTransform>();rect.bounds=new UnityEngine.Bounds{min=new UnityEngine.Vector2(0,-50),max=new UnityEngine.Vector2(0,20)};
   UnityEngine.Time.unscaledTime=1;HouseholdLibraryHandler.OnFocusGained(rows[0]);
   Check(w.ScrollRect.content.anchoredPosition.y==50&&w.ScrollRect.stops==1,"off-screen card is revealed in the native viewport before speech");
   Check(HouseholdLibraryHandler.ShouldSuppressScrollHover(children[0][0]),"stationary-pointer hover after reveal cannot interrupt keyboard focus");
   UnityEngine.InputSystem.Mouse.current.delta.value=new UnityEngine.Vector2(1,0);
   Check(!HouseholdLibraryHandler.ShouldSuppressScrollHover(children[0][0]),"real pointer movement retains normal hover speech");
   UnityEngine.InputSystem.Mouse.current.delta.value=new UnityEngine.Vector2(0,0);UnityEngine.Time.unscaledTime=2;
   Check(!HouseholdLibraryHandler.ShouldSuppressScrollHover(children[0][0]),"scroll hover suppression expires");
   return count+" household-library checks passed; native focus, selection and speech still need an in-game retest.";
  }
 }
}
'@
Add-Type -TypeDefinition ($handler + "`n" + $adapters) -Language CSharp
[ParalivesAccess.Handlers.LibraryChecks]::Run()
