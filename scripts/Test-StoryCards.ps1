$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\WindowHandler.cs')
$start = $source.IndexOf('private static string DescribeStoryCardWindow(')
if ($start -lt 0) { throw 'Missing story-card opening summary.' }
$body = $source.IndexOf('{', $start)
$depth = 1
$end = $body + 1
while ($depth -gt 0) {
    if ($source[$end] -eq '{') { $depth++ }
    if ($source[$end] -eq '}') { $depth-- }
    $end++
}
$method = $source.Substring($start, $end - $start)
# Compile the actual opening reader. UI visibility and pooled list membership are modeled here;
# audible delivery and native animations remain in-game checks.
$adapters = @'
using System;
using System.Collections.Generic;
public class GameObject {public bool activeInHierarchy=true;}
public class Component {
 public Component parent;public GameObject gameObject=new GameObject();public string label;
 public T GetComponentInParent<T>() where T:Component {
  for(Component node=this;node!=null;node=node.parent)if(node is T result)return result;return null;
 }
}
public class Focusable:Component {public bool skip;}
public class FocusableGroup:Component {public int count;}
public class UIContainer:FocusableGroup {public bool IsVisible=true;}
public class UIListItemBase:Focusable {}
public class UIList {public List<UIListItemBase> CurrentItems=new List<UIListItemBase>();}
public class UIStoryCard:UIListItemBase {}
public class UIStoryCardOffer:UIListItemBase {public UIContainer UIContainer;public UIList UIListCardsOffered=new UIList();}
public class UIStoryCards:FocusableGroup {public bool IsVisible=true;public UIContainer TutorialOverlay;}
public static class MenuNavigationHandler {public static Focusable Cursor;public static FocusableGroup CursorGroup;}
public static class FocusNavigator {public static bool ShouldSkip(Focusable f)=>f.skip;}
public static class UiLabelReader {public static string GetLabelOrFallback(Component c)=>c.label;public static string GetLabel(Component c)=>c.label;}
public static class Loc {public static string Get(string key,params object[] args)=>key+":"+string.Join("|",args);}
public static class StoryCardChecks {
 static int checks;static int CountFocusables(FocusableGroup g)=>g.count;
 static void Check(bool condition,string label){if(!condition)throw new Exception(label);checks++;}
 public static string Run(){
  var window=new UIStoryCards();var offer=new UIStoryCardOffer{parent=window};
  var group=new UIContainer{parent=offer,count=1};offer.UIContainer=group;
  var pack=new UIStoryCard{parent=group,label="Sun Pack. Cards that grant temporary buffs for 24 hours"};
  offer.UIListCardsOffered.CurrentItems.Add(pack);MenuNavigationHandler.CursorGroup=group;MenuNavigationHandler.Cursor=pack;
  string prefix="window_opened_one:Story cards";
  Check(DescribeStoryCardWindow(window,"Story cards")=="panel_with_focus:"+prefix+"|"+pack.label,"single pack is named on entry without requiring arrows to move");
  pack.label="Life Goals Pack";
  Check(DescribeStoryCardWindow(window,"Story cards").EndsWith("|Life Goals Pack"),"reused pack reads its current description");
  var card=new UIStoryCard{parent=group,label="You are in great shape"};offer.UIListCardsOffered.CurrentItems.Add(card);group.count=2;MenuNavigationHandler.Cursor=card;
  Check(DescribeStoryCardWindow(window,"Story cards")=="panel_with_focus:window_opened:Story cards|2|You are in great shape","multi-card hand names the actual selected card");
  offer.UIListCardsOffered.CurrentItems.Remove(card);
  Check(DescribeStoryCardWindow(window,"Story cards")=="window_opened:Story cards|2","stale pooled card is not spoken");
  offer.UIListCardsOffered.CurrentItems.Add(card);card.gameObject.activeInHierarchy=false;
  Check(DescribeStoryCardWindow(window,"Story cards")=="window_opened:Story cards|2","inactive card is not spoken");
  card.gameObject.activeInHierarchy=true;card.skip=true;
  Check(DescribeStoryCardWindow(window,"Story cards")=="window_opened:Story cards|2","unreachable card is not spoken");card.skip=false;
  group.IsVisible=false;Check(DescribeStoryCardWindow(window,"Story cards")==null,"hidden hand cannot contribute text");group.IsVisible=true;
  Check(DescribeStoryCardWindow(new UIStoryCards(),"Story cards")==null,"another window cannot contribute a card label");
  window.IsVisible=false;Check(DescribeStoryCardWindow(window,"Story cards")==null,"closed story window is silent");window.IsVisible=true;
  window.TutorialOverlay=new UIContainer{label="Choose a life goal"};
  Check(DescribeStoryCardWindow(window,"Story cards").EndsWith("|Choose a life goal"),"visible authored tutorial guidance is retained");
  window.TutorialOverlay.IsVisible=false;
  Check(!DescribeStoryCardWindow(window,"Story cards").Contains("Choose a life goal"),"hidden tutorial copy is excluded");
  Check(MenuNavigationHandler.Cursor==card&&MenuNavigationHandler.CursorGroup==group,"description does not change selection");
  return checks+" story-card opening checks passed; native speech and animations remain pending.";
 }
'@
Add-Type -TypeDefinition ($adapters + "`n" + $method + "`n}") -Language CSharp
[StoryCardChecks]::Run()
$settled = $source.Substring($source.IndexOf('private static string DescribeSettledWindow('))
$seed = $settled.IndexOf('MenuNavigationHandler.FocusForegroundWindow(window);')
$summary = $settled.IndexOf('DescribeStoryCardWindow(storyCards, name);')
$generic = $settled.IndexOf('string text = UiLabelReader.GetLabel(window);')
if ($seed -lt 0 -or $summary -le $seed -or $generic -le $summary) { throw 'Story-card summary is not wired after focus seeding and before hierarchy text reading.' }
Write-Host 'Story-card announcement lifecycle wiring check passed.'
