$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
function Read-Method([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature)
    if ($start -lt 0) { throw "Missing production method: $signature" }
    $body = $source.IndexOf('{', $start); $depth = 1; $end = $body + 1
    while ($depth -gt 0) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    $source.Substring($start, $end - $start)
}
$window = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\WindowHandler.cs')
$town = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\TownMapLotHandler.cs')
$report = Read-Method $window 'private static string BuildContextReport('
$lotContext = Read-Method $town 'internal static bool TryGetContext('
# Exercise actual report construction and selected/history lot resolution. Data readers and
# native ownership are adapters; no input/speech timing or game state is altered by this test.
$adapters = @'
using System;
using System.Collections.Generic;
public class GameObject {public bool activeInHierarchy=true;}
public class Focusable {public GameObject gameObject=new GameObject();public string Label;public bool Skip;}
public class FocusableGroup {public UIWindow Owner;public Focusable Focused;public string Name="Panel";public T GetComponentInParent<T>() where T:class=>Owner as T;public Focusable GetFocused(bool shoulder)=>Focused;}
public class UIWindow:FocusableGroup {public UIWindow(){Owner=this;}}
public class UILotInfo:UIWindow {public string Details;}
public class UIOccupationsChangeSchedule:UIWindow {}
public class UICharacters:UIWindow {}
public class UIPremadeHouseholdSelection:UIWindow {}
public class UIManager {public UIWindow CurrentWindow;}
public class HybridPlayer {public UIManager UIManager=new UIManager();}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public static class UI {public static T GetOrNull<T>(int player) where T:class=>null;}
namespace ParalivesAccess.Handlers {
 public static class Loc {public static string Get(string key,params object[] values)=>key+"("+string.Join("|",values)+")";}
 public static class MenuNavigationHandler {public static FocusableGroup ActivePanel,CursorGroup;public static Focusable Cursor;public static string GetPanelDisplayName(FocusableGroup panel)=>panel.Name;}
 public static class FocusNavigator {public static bool ShouldSkip(Focusable f)=>f.Skip;}
 public static class UiLabelReader {public static string GetLabelOrFallback(Focusable f)=>f.Label;}
 public static class LotReadinessReader {public static string DescribeLotInfo(UILotInfo info,bool includeReadiness){if(includeReadiness)throw new Exception("F3 requested inventory");return info.Details;}}
 public static class TutorialHandler {public static string Guidance;public static string GetCurrentGuidance()=>Guidance;}
 public static class OccupationHandler {public static bool TryGetContext(out string panel,out string element){panel=element=null;return false;}}
 public static class ContextActionHandler {public static bool TryGetContext(out string panel,out string element){panel=element=null;return false;}}
 public static class TownMapLotHandler {
  public class Entry {public string Text;public virtual string Describe()=>Text;}
  public class LotEntry:Entry {public bool IsAvailable=true;public string Details;public string DescribeDetails()=>Details;}
  public class Panel {public string Name="Places to visit";public Entry Current;}
  public class ViewState {public Entry Preferred;}
  public static bool Enabled=true,OwnsInput=true,Usable=true;
  public static Panel _panel=new Panel();public static Stack<ViewState> _history=new Stack<ViewState>();
  private static bool IsTownMapUsable(HybridPlayer player)=>Usable;
  LOT_CONTEXT
 }
 public static class LotContextChecks {
  REPORT_METHOD
  private static string DescribeWindowName(UIWindow window)=>window.Name;
  static int checks;
  static void Check(bool pass,string label){if(!pass)throw new Exception(label);checks++;}
  public static string Run(){
   var player=PlayerManager.Instance.HybridPlayer1;
   var native=new UIWindow{Name="Pause"};MenuNavigationHandler.ActivePanel=native;MenuNavigationHandler.CursorGroup=native;MenuNavigationHandler.Cursor=new Focusable{Label="Resume"};
   var lot=new TownMapLotHandler.LotEntry{Details="50 Leblanc Street. Community Center. Hours: 7 to 23."};
   TownMapLotHandler._panel.Current=lot;
   Check(BuildContextReport(player)==lot.Details,"selected lot speaks details directly without screen/panel introduction");
   TownMapLotHandler._history.Push(new TownMapLotHandler.ViewState{Preferred=lot});TownMapLotHandler._panel.Current=new TownMapLotHandler.Entry{Text="Visit"};
   Check(BuildContextReport(player)==lot.Details,"command page reads its owning lot directly");
   lot.Details="Updated address. Community Center";Check(BuildContextReport(player)==lot.Details,"lot details are read again on each request");
   lot.IsAvailable=false;Check(BuildContextReport(player)==Loc.Get("lots_target_gone"),"removed lot retains its explicit unavailable response");lot.IsAvailable=true;
   lot.Details=" ";Check(BuildContextReport(player).Contains("context_no_focus"),"empty lot details fall back to an audible context response");
   TownMapLotHandler._history.Clear();TownMapLotHandler._panel.Current=new TownMapLotHandler.Entry{Text="Households"};
   Check(BuildContextReport(player)==Loc.Get("context_report",Loc.Get("lots_panel"),"Places to visit","Households"),"category browsing still reports orientation when no lot is selected");
   TownMapLotHandler._panel.Current=lot;TownMapLotHandler.OwnsInput=false;
   Check(BuildContextReport(player)==Loc.Get("context_report","Pause","Pause","Resume"),"foreground native window owns F3 when L is suspended");
   var info=new UILotInfo{Name="Lot Info",Details="212 Souvenir Street. Residence. Visit"};MenuNavigationHandler.ActivePanel=info;MenuNavigationHandler.CursorGroup=info;MenuNavigationHandler.Cursor=new Focusable{Label="Close"};
   Check(BuildContextReport(player)==info.Details,"native Lot Info reports details without unrelated focused-control introduction");
   TutorialHandler.Guidance="Purchase your home";
   Check(BuildContextReport(player)==Loc.Get("context_with_tutorial",info.Details,TutorialHandler.Guidance),"native Lot Info retains applicable tutorial guidance");TutorialHandler.Guidance=null;
   info.Details=null;Check(BuildContextReport(player)==Loc.Get("context_report","Lot Info","Lot Info","Close"),"missing native lot details retain normal context fallback");
   TownMapLotHandler.Enabled=false;TownMapLotHandler.OwnsInput=true;MenuNavigationHandler.ActivePanel=native;MenuNavigationHandler.CursorGroup=native;MenuNavigationHandler.Cursor=new Focusable{Label="Resume"};
   Check(BuildContextReport(player)==Loc.Get("context_report","Pause","Pause","Resume"),"disabled L handler cannot hide the ordinary context report");
   return checks+" lot-context checks passed; audible delivery remains a runtime check.";
  }
 }
}
'@
$adapters = $adapters.Replace('LOT_CONTEXT', $lotContext).Replace('REPORT_METHOD', $report)
Add-Type -TypeDefinition $adapters -Language CSharp
[ParalivesAccess.Handlers.LotContextChecks]::Run()
