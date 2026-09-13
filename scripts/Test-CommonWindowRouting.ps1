$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\MenuNavigationHandler.cs')
function Read-Method([string]$signature) {
    $start = $source.IndexOf($signature)
    if ($start -lt 0) { throw "Missing production method: $signature" }
    $body = $source.IndexOf('{', $start)
    $depth = 1
    $end = $body + 1
    while ($depth -gt 0) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    $source.Substring($start, $end - $start)
}
$methods = @(
    'private static UIWindow ResolveGameDrivenWindow('
    'private static bool IsBlockingCommonWindow('
    'private static void BuildCandidates('
    'private static int CompareBySiblingIndex('
    'private static FocusableGroup ResolveActiveGroup('
    'internal static bool FocusForegroundWindow('
    'private static FocusableGroup FindForegroundGroup('
) | ForEach-Object { Read-Method $_ }
$frameStart = $source.IndexOf('                UIWindow gameDrivenWindow = ResolveGameDrivenWindow(hybridPlayer);')
$frameEnd = $source.IndexOf('                HandlePanelSwitching(capturedPanelDirection);', $frameStart)
$frame = $source.Substring($frameStart, $frameEnd - $frameStart)
# Test the production window selection and foreground stack against separate native manager lists.
# Rendering, popup callbacks and audible output are intentionally outside this adapter.
$adapters = @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
public class GameObject { public bool activeInHierarchy=true; }
public class Transform { public int index; public int GetSiblingIndex()=>index; }
public class FocusableGroup { public string name; public int count; public Transform transform=new Transform(); public GameObject gameObject=new GameObject(); }
public class UIWindow:FocusableGroup {
 public bool IsVisible=true,BlockPlayerActionWhenShown,HideOtherInterfacesWhenShown;
 public List<FocusableGroup> children=new List<FocusableGroup>();
 public T[] GetComponentsInChildren<T>(bool includeInactive) where T:FocusableGroup {
  var all=new List<T>(); if(this is T t)all.Add(t);
  foreach(var c in children)if((includeInactive||c.gameObject.activeInHierarchy)&&c is T item)all.Add(item);
  return all.ToArray();
 }
}
public class UIManager { private List<UIWindow> _visibleWindows=new List<UIWindow>(); public UIWindow CurrentWindow; public void Add(UIWindow w)=>_visibleWindows.Add(w); }
public class HybridPlayer { public UIManager UIManager=new UIManager(); }
public class HybridReferences { public static HybridReferences Instance=new HybridReferences(); public UIManager UIManagerCommon=new UIManager(); }
public static class Plugin { public static LogAdapter Log=new LogAdapter(); }
public class LogAdapter { public void LogInfo(string s){} }
public static class FocusHandler { public static bool AnnounceFocus=true; }
public static class CommonWindowChecks {
 static List<FocusableGroup> _candidates=new List<FocusableGroup>();
 static List<UIWindow> _windowBuffer=new List<UIWindow>();
 static FieldInfo _visibleWindowsField;
 static FocusableGroup _selectedGroup;
 static bool _selectionIsExplicit,_selectionIsTutorialDirected,_selectionIsForegroundDirected;
 struct SuspendedSelection { public FocusableGroup Group;public bool IsExplicit,IsTutorialDirected,IsForegroundDirected; }
 static Stack<SuspendedSelection> _suspendedSelections=new Stack<SuspendedSelection>();
 static void ResolveReflection(){_visibleWindowsField=typeof(UIManager).GetField("_visibleWindows",BindingFlags.NonPublic|BindingFlags.Instance);}
 static int CountFocusables(FocusableGroup g)=>g.count;
 static void SeedFocus(FocusableGroup g){}
 static int checks;
 static void Check(bool condition,string label){if(!condition)throw new Exception(label);checks++;}
 public static string Run(){
  var player=new HybridPlayer();var settings=new UIWindow{name="Options",count=12};var gameBar=new UIWindow{name="GameBar",count=3};
  player.UIManager.Add(settings);player.UIManager.Add(gameBar);player.UIManager.CurrentWindow=settings;
  _selectedGroup=settings;_selectionIsForegroundDirected=true;
  var popup=new UIWindow{name="ContextMenu",BlockPlayerActionWhenShown=true,count=8};
  HybridReferences.Instance.UIManagerCommon.CurrentWindow=popup;
  Check(Frame(player)==popup,"shared dropdown takes focus from the local Options window");
  Check(_candidates.Count==1&&_candidates[0]==popup,"background settings and Game Bar are excluded while dropdown blocks actions");
  Check(_suspendedSelections.Count==1,"opening dropdown preserves the Settings selection");
  Frame(player);Check(_suspendedSelections.Count==1,"subsequent frames do not stack duplicate return selections");
  popup.IsVisible=false;HybridReferences.Instance.UIManagerCommon.CurrentWindow=null;
  Check(Frame(player)==settings&&_selectionIsForegroundDirected,"closing dropdown restores Settings with its ownership intact");
  Check(_candidates.Count==2&&_candidates.Contains(gameBar),"ordinary local candidates return after popup closes");
  popup.IsVisible=true;popup.count=0;HybridReferences.Instance.UIManagerCommon.CurrentWindow=popup;
  Check(Frame(player)==null&&_selectedGroup==settings,"empty common popup blocks background input without discarding return selection");
  popup.count=8;Check(Frame(player)==popup&&_suspendedSelections.Count==1,"populated popup acquires focus and preserves Settings");
  popup.IsVisible=false;HybridReferences.Instance.UIManagerCommon.CurrentWindow=null;Frame(player);
  _selectionIsExplicit=true;_selectionIsForegroundDirected=false;
  popup.IsVisible=true;HybridReferences.Instance.UIManagerCommon.CurrentWindow=popup;
  Check(Frame(player)==popup,"blocking shared dropdown overrides an explicit background panel selection");
  popup.IsVisible=false;HybridReferences.Instance.UIManagerCommon.CurrentWindow=null;
  Check(Frame(player)==settings&&_selectionIsExplicit,"closing dropdown restores an explicit selection");
  popup.IsVisible=true;popup.BlockPlayerActionWhenShown=false;HybridReferences.Instance.UIManagerCommon.CurrentWindow=popup;
  Check(Frame(player)==settings&&_candidates.Contains(popup)&&_candidates.Count==3,"nonblocking common windows are included without taking exclusive focus");
  popup.HideOtherInterfacesWhenShown=true;
  Check(Frame(player)==popup&&_candidates.Count==1,"a common window hiding other interfaces is also exclusive");
  return checks+" common-window routing checks passed.";
 }
'@
$program = $adapters + "`n" + ($methods -join "`n") + "`nprivate static FocusableGroup Frame(HybridPlayer hybridPlayer) {`n" + $frame + "return ResolveActiveGroup(gameDrivenWindow); }`n}"
Add-Type -TypeDefinition $program -Language CSharp
[CommonWindowChecks]::Run()
