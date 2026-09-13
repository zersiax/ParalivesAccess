$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$coordinator = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Speech\AnnouncementCoordinator.cs')
$zoom = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\CameraZoomHandler.cs')
$navigator = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\FocusNavigator.cs')
$window = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\WindowHandler.cs')
# Compile production code against small input/output adapters. No Unity process or native speech
# library is involved; these checks establish routing and ordering, not in-game audibility.
$zoom = $zoom.Substring($zoom.IndexOf('namespace ParalivesAccess.Handlers'))
$direction = $navigator.Substring($navigator.IndexOf('        public static Vector2 ReadDirection('))
$direction = $direction.Substring(0, $direction.LastIndexOf('    }'))
$transition = $window.Substring($window.IndexOf('        internal static bool IsSceneTransitionActive'))
$transition = $transition.Substring(0, $transition.IndexOf('        private static bool IsOwnedCreatorOutfits'))
$adapters = @'
namespace UnityEngine {
 public static class Time { public static float unscaledTime; }
 public struct Vector2 {
  public float x,y; public Vector2(float a,float b){x=a;y=b;}
  public float sqrMagnitude => x*x+y*y;
  public Vector2 normalized => sqrMagnitude==0 ? zero : new Vector2(x/(float)System.Math.Sqrt(sqrMagnitude),y/(float)System.Math.Sqrt(sqrMagnitude));
  public static Vector2 zero => new Vector2(0,0);
  public static bool operator ==(Vector2 a,Vector2 b)=>a.x==b.x&&a.y==b.y;
  public static bool operator !=(Vector2 a,Vector2 b)=>!(a==b);
  public override bool Equals(object o)=>o is Vector2 v&&this==v;
  public override int GetHashCode()=>x.GetHashCode()^y.GetHashCode();
 }
 public static class Mathf {
  public static bool Approximately(float a,float b)=>System.Math.Abs(a-b)<0.000001f;
  public static int RoundToInt(float n)=>(int)System.Math.Round(n);
 }
}
namespace UnityEngine.InputSystem.Controls {
 public class ButtonControl:UnityEngine.InputSystem.InputControl { public bool isPressed,wasPressedThisFrame,wasReleasedThisFrame,IntervalPressed; }
 public class KeyControl:ButtonControl { public UnityEngine.InputSystem.Key keyCode; }
 public class VectorControl { public UnityEngine.Vector2 value; public UnityEngine.Vector2 ReadValue()=>value; }
}
namespace UnityEngine.InputSystem {
 public class InputControl {}
 public class Gamepad {
  public static Gamepad current;
  public InputControl[] allControls=new[]{new Controls.ButtonControl()};
  public Controls.VectorControl leftStick=new Controls.VectorControl(),rightStick=new Controls.VectorControl();
 }
 public enum Key { A, LeftCtrl,RightCtrl,LeftShift,RightShift,LeftAlt,RightAlt,LeftMeta,RightMeta }
 public class Keyboard { public static Keyboard current=new Keyboard(); public Controls.KeyControl[] allKeys=new[]{new Controls.KeyControl()}; }
 public class Mouse {
  public static Mouse current=new Mouse();
  public Controls.KeyControl leftButton=new Controls.KeyControl(),rightButton=new Controls.KeyControl(),middleButton=new Controls.KeyControl();
  public Controls.VectorControl delta=new Controls.VectorControl(),scroll=new Controls.VectorControl();
 }
}
public enum State { MainMenu, Loading, Game }
public enum GameStates { LiveMode, CharacterCreator }
public static class GameLoadingManager { public static State State; }
public class Player { public GameStates State=GameStates.LiveMode; public bool IsUsingFreeCamera=true; public int PlayerIndex; public float AudioSystemCameraZoomRatio; }
public class Axis { public float Value; }
public class DirectionInput { public UnityEngine.Vector2 Value; public bool IntervalPressed; }
public class CameraFree { public float TargetDistance; }
public class CameraHybrid { public CameraFree FreeCamera=new CameraFree(); }
public class HybridPlayer {
 public UIManager UIManager=new UIManager();
 public Player Player=new Player(); public bool HasInputFieldFocused; public Axis Zoom=new Axis(); public CameraHybrid HybridCamera=new CameraHybrid();
 public DirectionInput CameraMovement=new DirectionInput();
 public UnityEngine.InputSystem.Controls.KeyControl ButtonDPadLeft=new UnityEngine.InputSystem.Controls.KeyControl(),ButtonDPadRight=new UnityEngine.InputSystem.Controls.KeyControl(),ButtonDPadDown=new UnityEngine.InputSystem.Controls.KeyControl(),ButtonDPadUp=new UnityEngine.InputSystem.Controls.KeyControl();
}
public class PlayerManager { public static PlayerManager Instance=new PlayerManager(); public HybridPlayer HybridPlayer1=new HybridPlayer(); public HybridPlayer GetHybridPlayer(int i)=>HybridPlayer1; }
public class FocusableGroup {
 public int Quantizations;
 public UnityEngine.Vector2 GetFourDirectionsInputVector2(UnityEngine.Vector2 v) {
  Quantizations++;
  // Native helper maps zero to Up. A release vector must never reach it.
  if(v==UnityEngine.Vector2.zero)return new UnityEngine.Vector2(0,1);
  return System.Math.Abs(v.x)>System.Math.Abs(v.y)?new UnityEngine.Vector2(System.Math.Sign(v.x),0):new UnityEngine.Vector2(0,System.Math.Sign(v.y));
 }
}
public class UIWindow { public bool IsVisible; }
public class UIPrompt:UIWindow {}
public class UIManager { public UIWindow CurrentWindow; }
public class HybridReferences { public static HybridReferences Instance=new HybridReferences(); public UIManager UIManagerCommon=new UIManager(); }
public class UITransitionFade:UIWindow {}
public class UILoadingScreen:UIWindow {}
public class UI {
 public static UI Instance=new UI(); public static System.Collections.Generic.Dictionary<string,UIWindow> Windows=new System.Collections.Generic.Dictionary<string,UIWindow>();
 public static T GetOrNull<T>(int player) where T:UIWindow { UIWindow w; return Windows.TryGetValue(typeof(T).Name+player,out w)?(T)w:null; }
}
namespace ParalivesAccess {
 public class Logger { public void LogInfo(string s){} public void LogError(string s){throw new System.Exception(s);} }
 public static class Plugin { public static Logger Log=new Logger(); }
 public static class Loc { public static string Get(string key,params object[] args)=>key+":"+string.Join(",",args); }
}
namespace ParalivesAccess.Handlers {public static class ContextKeyInput {public static bool ContextPressed,RepeatPressed;}}
namespace ParalivesAccess.Speech {
 public static class ScreenReader {
  public struct Output { public string Text; public bool Interrupt; }
  public static System.Collections.Generic.List<Output> Sent=new System.Collections.Generic.List<Output>();
  public static string NormalizeForOutput(string s)=>s;
  public static bool Speak(string s,bool interrupt=true) {
   interrupt=AnnouncementCoordinator.ResolveInterrupt(interrupt);
   Sent.Add(new Output{Text=s,Interrupt=interrupt});
   if(interrupt)AnnouncementCoordinator.NoteForegroundSpeech();
   return true;
  }
 }
}
public static class RegressionChecks {
 static int checks;
 static void Check(bool condition,string name){if(!condition)throw new System.Exception("FAIL: "+name);checks++;}
 static void Reset(){
  ParalivesAccess.Handlers.ContextKeyInput.ContextPressed=false;ParalivesAccess.Handlers.ContextKeyInput.RepeatPressed=false;
  UnityEngine.InputSystem.Keyboard.current=new UnityEngine.InputSystem.Keyboard();UnityEngine.InputSystem.Mouse.current=new UnityEngine.InputSystem.Mouse();
  GameLoadingManager.State=State.MainMenu;ParalivesAccess.Speech.AnnouncementCoordinator.Tick();GameLoadingManager.State=State.Game;
  UnityEngine.Time.unscaledTime+=10;ParalivesAccess.Speech.ScreenReader.Sent.Clear();UI.Windows.Clear();
  UnityEngine.InputSystem.Gamepad.current=null;HybridReferences.Instance.UIManagerCommon.CurrentWindow=null;PlayerManager.Instance.HybridPlayer1.UIManager.CurrentWindow=null;
 }
 static void DeliverTutorial(string text="Tutorial finished"){
  ParalivesAccess.Speech.AnnouncementCoordinator.PublishLatestImportant("tutorial",text);
  UnityEngine.Time.unscaledTime+=1;ParalivesAccess.Speech.AnnouncementCoordinator.Tick();
 }
 public static string Run(){
  Reset();var sent=ParalivesAccess.Speech.ScreenReader.Sent;
  ParalivesAccess.Speech.ScreenReader.Speak("foreground");
  ParalivesAccess.Speech.AnnouncementCoordinator.PublishLatestImportant("tutorial","first lesson");
  ParalivesAccess.Speech.AnnouncementCoordinator.Tick();Check(sent.Count==1,"foreground quiet gate");
  UnityEngine.Time.unscaledTime+=1;ParalivesAccess.Speech.AnnouncementCoordinator.Tick();Check(sent.Count==2&&!sent[1].Interrupt,"tutorial queued");
  UnityEngine.Time.unscaledTime+=2;ParalivesAccess.Speech.ScreenReader.Speak("Eat ended");ParalivesAccess.Speech.ScreenReader.Speak("Town Map");
  Check(!sent[2].Interrupt&&!sent[3].Interrupt,"later automatic action and window preserve tutorial");
  UnityEngine.InputSystem.Keyboard.current.allKeys[0].wasPressedThisFrame=true;
  ParalivesAccess.Speech.ScreenReader.Speak("next focused control");Check(sent[4].Interrupt,"new key preempts before driver tick");
  Reset();DeliverTutorial();ParalivesAccess.Handlers.ContextKeyInput.ContextPressed=true;
  ParalivesAccess.Speech.ScreenReader.Speak("F3 context");Check(sent[1].Interrupt,"retained fast F3 tap preempts protected speech without frame-edge flags");
  Reset();DeliverTutorial();ParalivesAccess.Handlers.ContextKeyInput.RepeatPressed=true;
  ParalivesAccess.Speech.ScreenReader.Speak("Shift F3 repeat");Check(sent[1].Interrupt,"retained fast Shift F3 tap is also fresh physical input");
  Reset();DeliverTutorial();UnityEngine.InputSystem.Mouse.current.delta.value=new UnityEngine.Vector2(2,0);
  ParalivesAccess.Speech.ScreenReader.Speak("mouse hover");Check(sent[1].Interrupt,"real pointer movement preempts");
  Reset();DeliverTutorial();UnityEngine.InputSystem.Gamepad.current=new UnityEngine.InputSystem.Gamepad();
  ((UnityEngine.InputSystem.Controls.ButtonControl)UnityEngine.InputSystem.Gamepad.current.allControls[0]).wasPressedThisFrame=true;
  ParalivesAccess.Speech.ScreenReader.Speak("controller focus");Check(sent[1].Interrupt,"controller button preempts");
  Reset();DeliverTutorial();UnityEngine.InputSystem.Gamepad.current=new UnityEngine.InputSystem.Gamepad();UnityEngine.InputSystem.Gamepad.current.leftStick.value=new UnityEngine.Vector2(1,0);
  ParalivesAccess.Speech.ScreenReader.Speak("controller navigation");Check(sent[1].Interrupt,"controller stick preempts");
  Reset();ParalivesAccess.Speech.AnnouncementCoordinator.PublishLatestImportant("tutorial","obsolete");
  DeliverTutorial("current");Check(sent.Count==1&&sent[0].Text=="current","newest pending lesson replaces old lesson");
  Reset();UnityEngine.InputSystem.Keyboard.current.allKeys[0].isPressed=true;UnityEngine.InputSystem.Keyboard.current.allKeys[0].keyCode=UnityEngine.InputSystem.Key.LeftAlt;
  DeliverTutorial();Check(sent.Count==1,"latched modifier does not starve tutorial");
  Reset();UnityEngine.InputSystem.Keyboard.current.allKeys[0].isPressed=true;DeliverTutorial();Check(sent.Count==0,"held navigation key delays tutorial");
  UnityEngine.InputSystem.Keyboard.current.allKeys[0].isPressed=false;UnityEngine.Time.unscaledTime+=1;ParalivesAccess.Speech.AnnouncementCoordinator.Tick();Check(sent.Count==1,"tutorial releases after navigation");
  GameLoadingManager.State=State.Loading;ParalivesAccess.Speech.AnnouncementCoordinator.Tick();GameLoadingManager.State=State.MainMenu;
  ParalivesAccess.Speech.ScreenReader.Speak("Main Menu");Check(sent[sent.Count-1].Interrupt,"scene exit clears speech protection");
  Reset();ParalivesAccess.Speech.AnnouncementCoordinator.PublishImportant("notification","message");UnityEngine.Time.unscaledTime+=1;ParalivesAccess.Speech.AnnouncementCoordinator.Tick();
  ParalivesAccess.Speech.ScreenReader.Speak("automatic");Check(sent[1].Interrupt,"ordinary event policy unchanged");
  var hybrid=PlayerManager.Instance.HybridPlayer1;var group=new FocusableGroup();hybrid.CameraMovement.IntervalPressed=true;
  Check(ParalivesAccess.Handlers.DirectionUnderTest.ReadDirection(group,hybrid)==UnityEngine.Vector2.zero&&group.Quantizations==0,"zero release never quantized to Up");
  hybrid.CameraMovement.Value=new UnityEngine.Vector2(-1,0);Check(ParalivesAccess.Handlers.DirectionUnderTest.ReadDirection(group,hybrid)==new UnityEngine.Vector2(-1,0),"held native direction retained");
  hybrid.ButtonDPadDown.IntervalPressed=true;Check(ParalivesAccess.Handlers.DirectionUnderTest.ReadDirection(group,hybrid)==new UnityEngine.Vector2(0,-1),"D-pad priority retained");
  Reset();Check(!ParalivesAccess.Handlers.WindowHandler.IsSceneTransitionActive,"loaded scene available");
  GameLoadingManager.State=State.Loading;Check(ParalivesAccess.Handlers.WindowHandler.IsSceneTransitionActive,"loading state blocks stale UI");
  HybridReferences.Instance.UIManagerCommon.CurrentWindow=new UIPrompt{IsVisible=true};Check(!ParalivesAccess.Handlers.WindowHandler.IsSceneTransitionActive,"common loading prompt remains accessible");
  HybridReferences.Instance.UIManagerCommon.CurrentWindow.IsVisible=false;Check(ParalivesAccess.Handlers.WindowHandler.IsSceneTransitionActive,"closed loading prompt does not release stale UI");
  hybrid.UIManager.CurrentWindow=new UIPrompt{IsVisible=true};Check(!ParalivesAccess.Handlers.WindowHandler.IsSceneTransitionActive,"player loading prompt remains accessible");
  hybrid.UIManager.CurrentWindow=null;GameLoadingManager.State=State.Game;
  UI.Windows["UITransitionFade0"]=new UITransitionFade{IsVisible=true};Check(ParalivesAccess.Handlers.WindowHandler.IsSceneTransitionActive,"fade blocks before loading state");
  UI.Windows["UITransitionFade0"].IsVisible=false;Check(!ParalivesAccess.Handlers.WindowHandler.IsSceneTransitionActive,"hidden pooled fade does not block");
  UI.Windows["UILoadingScreen-1"]=new UILoadingScreen{IsVisible=true};Check(ParalivesAccess.Handlers.WindowHandler.IsSceneTransitionActive,"common loading window blocks");
  Reset();var p=hybrid.Player;hybrid.Zoom.Value=1;hybrid.HybridCamera.FreeCamera.TargetDistance=20;
  float before=ParalivesAccess.Handlers.CameraZoomHandler.BeforeCameraUpdate(p);
  ParalivesAccess.Handlers.CameraZoomHandler.AfterCameraUpdate(p,before);hybrid.Zoom.Value=0;ParalivesAccess.Handlers.CameraZoomHandler.Tick();Check(sent.Count==0,"blocked or clamped input reports no invented change");
  hybrid.Zoom.Value=1;before=ParalivesAccess.Handlers.CameraZoomHandler.BeforeCameraUpdate(p);hybrid.HybridCamera.FreeCamera.TargetDistance=10;p.AudioSystemCameraZoomRatio=.6f;
  ParalivesAccess.Handlers.CameraZoomHandler.AfterCameraUpdate(p,before);ParalivesAccess.Handlers.CameraZoomHandler.Tick();Check(sent.Count==0,"held zoom stays quiet");
  before=ParalivesAccess.Handlers.CameraZoomHandler.BeforeCameraUpdate(p);hybrid.HybridCamera.FreeCamera.TargetDistance=8;p.AudioSystemCameraZoomRatio=.4f;
  ParalivesAccess.Handlers.CameraZoomHandler.AfterCameraUpdate(p,before);hybrid.Zoom.Value=0;ParalivesAccess.Handlers.CameraZoomHandler.Tick();
  Check(sent.Count==1&&sent[0].Text=="camera_zoom_value:60","one final native zoom value");ParalivesAccess.Handlers.CameraZoomHandler.Tick();Check(sent.Count==1,"no repeated zoom chatter");
  Check(float.IsNaN(ParalivesAccess.Handlers.CameraZoomHandler.BeforeCameraUpdate(p)),"automatic camera framing is not zoom input");
  return checks+" routing and ordering checks passed; Unity/Prism runtime validation remains pending.";
 }
}
'@
$source = "using ParalivesAccess.Speech;`n" + $coordinator + "`n" + $zoom + "`n" + $adapters + "`nnamespace ParalivesAccess.Handlers { public static class DirectionUnderTest {`n" + $direction + "`n} public static class WindowHandler {`n" + $transition + "`n} }"
Add-Type -TypeDefinition $source -Language CSharp
[RegressionChecks]::Run()

$locSource = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Loc.cs')
$defined = @{}
[regex]::Matches($locSource, 'Add\("([^"]+)"') | ForEach-Object { $defined[$_.Groups[1].Value] = $true }
$missing = @(Get-ChildItem (Join-Path $projectRoot 'src') -Recurse -Filter '*.cs' | ForEach-Object {
 $text = Get-Content -Raw -Encoding UTF8 $_.FullName
 $text = [regex]::Replace($text, '(?m)^\s*//.*$', '')
 [regex]::Matches($text, 'Loc\.Get\("([^"]+)"') | ForEach-Object { if (-not $defined.ContainsKey($_.Groups[1].Value)) { $_.Groups[1].Value } }
} | Sort-Object -Unique)
if ($missing.Count -gt 0) { throw ('Unresolved literal localization keys: ' + ($missing -join ', ')) }
Write-Output 'All literal localization keys resolve.'
