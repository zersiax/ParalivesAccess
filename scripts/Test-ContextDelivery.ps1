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
$probe = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Probes/ContextDeliveryProbe.cs')
$speech = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Speech/ScreenReader.cs')
$methods = @(
    'public static bool Speak(string text)',
    'public static bool Speak(string text, bool interrupt)',
    'internal static bool Speak(string text, bool interrupt, string diagnosticSource)',
    'public static void Stop()'
) | ForEach-Object { Read-Method $speech $_ }
# Production event observer and native-output dispatcher, with explicit keyboard and Prism
# adapters. Verify that instrumentation observes fast events without changing dispatch semantics.
$adapters = @'
namespace UnityEngine {
 public static class Time {public static int frameCount;public static float unscaledTime;}
 public static class Application {public static bool isFocused=true;}
}
namespace UnityEngine.InputSystem.LowLevel {
 public struct StateEvent {}
 public struct DeltaStateEvent {}
 public struct InputEventPtr {
  public System.Type Kind;public float? F3;public double time;public bool handled;
  public bool IsA<T>()=>Kind==typeof(T);
 }
}
namespace UnityEngine.InputSystem {
 public class InputDevice {public int deviceId;}
 public class Button {public bool isPressed,wasPressedThisFrame,wasReleasedThisFrame;
  public bool ReadValueFromEvent(LowLevel.InputEventPtr e,out float v){v=e.F3??0;return e.F3.HasValue;}
  public bool IsValueConsideredPressed(float v)=>v>=0.5f;
 }
 public class Keyboard:InputDevice {public static Keyboard current;public Button f3Key=new Button(),leftShiftKey=new Button(),rightShiftKey=new Button();}
 public static class InputSystem {
  public static event System.Action<LowLevel.InputEventPtr,InputDevice> onEvent;
  public static int Subscribers=>onEvent?.GetInvocationList().Length??0;
  public static void Send(LowLevel.InputEventPtr e,InputDevice device){onEvent?.Invoke(e,device);}
 }
}
public enum State {Game,Loading}
public static class GameLoadingManager {public static State State=State.Game;}
public class HybridPlayer {public bool HasInputFieldFocused;}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
namespace ParalivesAccess {
 public static class Plugin {public static Logger Log=new Logger();}
 public class Logger {
  public readonly System.Collections.Generic.List<string> Lines=new System.Collections.Generic.List<string>();
  public void LogInfo(string s){Lines.Add(s);}public void LogError(string s){Lines.Add(s);}public void LogWarning(string s){Lines.Add(s);}
 }
}
namespace ParalivesAccess.Handlers {public static class SettingsHandler {public static bool IsCapturingBindingInput;}}
namespace ParalivesAccess.Speech {
 public static class AnnouncementCoordinator {
  public static bool ForceQueue;public static int ForegroundCalls;
  public static bool ResolveInterrupt(bool b)=>b&&!ForceQueue;
  public static void NoteForegroundSpeech(){ForegroundCalls++;}
 }
 public static class PrismNative {
  public enum PrismError {Ok,Failure,NotSpeaking}
  public static PrismError Result;public static bool Throw;
  public static int Outputs,Speaks,Stops;public static bool Interrupt;public static string Text;
  public static byte[] ToUtf8(string s)=>System.Text.Encoding.UTF8.GetBytes(s);
  static PrismError Send(byte[] b,bool interrupt){Interrupt=interrupt;Text=System.Text.Encoding.UTF8.GetString(b);if(Throw)throw new InvalidOperationException("native test failure");return Result;}
  public static PrismError prism_backend_output(IntPtr backend,byte[] b,bool interrupt){Outputs++;return Send(b,interrupt);}
  public static PrismError prism_backend_speak(IntPtr backend,byte[] b,bool interrupt){Speaks++;return Send(b,interrupt);}
  public static PrismError prism_backend_stop(IntPtr backend){Stops++;return Result;}
  public static string DescribeError(PrismError e)=>e.ToString();
 }
 public static class ScreenReader {
  static IntPtr _backend=new IntPtr(1);internal static bool _useOutput=true;static string _lastSpoken;
  public static bool IsAvailable=true;public static string BackendName="NVDA";
  static string Clean(string s)=>s?.Trim();
'@
$checks = @'
 }
}
namespace ParalivesAccess.Probes {
 public static class ContextChecks {
  static int count;static void Check(bool b,string s){if(!b)throw new Exception(s);count++;}
  static bool Logged(string s)=>Plugin.Log.Lines.Exists(x=>x.Contains(s));
  static void Send(float? f3,Type kind=null,int device=1,bool handled=false){
   UnityEngine.InputSystem.InputSystem.Send(new UnityEngine.InputSystem.LowLevel.InputEventPtr{
    Kind=kind??typeof(UnityEngine.InputSystem.LowLevel.StateEvent),F3=f3,time=Time.unscaledTime,handled=handled},
    new UnityEngine.InputSystem.Keyboard{deviceId=device});
  }
  public static string Run(){
   var k=new UnityEngine.InputSystem.Keyboard{deviceId=1};UnityEngine.InputSystem.Keyboard.current=k;
   ContextDeliveryProbe.Enabled=false;ContextDeliveryProbe.Install();Check(UnityEngine.InputSystem.InputSystem.Subscribers==0,"disabled audit does not subscribe");
   ContextDeliveryProbe.Enabled=true;ContextDeliveryProbe.Install();ContextDeliveryProbe.Install();Check(UnityEngine.InputSystem.InputSystem.Subscribers==1,"audit subscribes once");
   Plugin.Log.Lines.Clear();Send(1);Send(0);ContextDeliveryProbe.BeforeGameSystems();
   Check(Logged("F3=down")&&Logged("F3=up"),"raw down and up in one update are both observed");
   Check(Logged("pressed=False, released=False"),"missed frame edges can be distinguished from absent keyboard events");
   Check(ParalivesAccess.Speech.PrismNative.Outputs==0,"input probe never speaks or dispatches a context action");
   int lines=Plugin.Log.Lines.Count;Send(0);Send(null);Send(1,typeof(string));Check(Plugin.Log.Lines.Count==lines,"unchanged, partial and non-state events are ignored");
   Send(1,typeof(UnityEngine.InputSystem.LowLevel.DeltaStateEvent),handled:true);Check(Logged("handled=True"),"handled delta events are observed without modifying them");
   lines=Plugin.Log.Lines.Count;Send(1);Check(Plugin.Log.Lines.Count==lines,"held F3 does not create duplicate raw presses");
   Send(1,device:2);Check(Logged("device=2"),"events from another keyboard are visible separately");
   Time.unscaledTime=1;k.f3Key.wasPressedThisFrame=true;k.leftShiftKey.isPressed=true;ContextDeliveryProbe.BeforeGameSystems();
   Check(Logged("shift=True"),"frame sampling reports modifier ownership");k.f3Key.wasPressedThisFrame=false;k.leftShiftKey.isPressed=false;
   Plugin.Log.Lines.Clear();
   bool ok=ParalivesAccess.Speech.ScreenReader.Speak("  private test text  ",true,"F3-context");
   Check(ok&&ParalivesAccess.Speech.PrismNative.Text=="private test text"&&ParalivesAccess.Speech.PrismNative.Interrupt,"normal context still reaches native output with its original text and interrupt flag");
   Check(Logged("source=F3-context")&&Logged("nativeResult=Ok"),"F3 native handoff and return are linked in the trace");
   Check(!Logged("private test text"),"audit records metadata without duplicating speech text");
   ParalivesAccess.Speech.ScreenReader.Speak("activity",false,"F3-activities");
   Check(!ParalivesAccess.Speech.PrismNative.Interrupt&&Logged("source=F3-activities"),"activity supplement retains noninterrupting delivery");
   ParalivesAccess.Speech.AnnouncementCoordinator.ForceQueue=true;ParalivesAccess.Speech.ScreenReader.Speak("focus",true);
   Check(Logged("requestedInterrupt=True, effectiveInterrupt=False")&&!ParalivesAccess.Speech.PrismNative.Interrupt,"coordinator flag changes are observed without overriding them");
   ParalivesAccess.Speech.AnnouncementCoordinator.ForceQueue=false;
   ParalivesAccess.Speech.ScreenReader._useOutput=false;ParalivesAccess.Speech.ScreenReader.Speak("speak-only");
   Check(ParalivesAccess.Speech.PrismNative.Speaks==1&&Logged("api=speak"),"speech-only backend dispatch is preserved");ParalivesAccess.Speech.ScreenReader._useOutput=true;
   ParalivesAccess.Speech.PrismNative.Result=ParalivesAccess.Speech.PrismNative.PrismError.Failure;
   Check(!ParalivesAccess.Speech.ScreenReader.Speak("failure")&&Logged("nativeResult=Failure"),"native failure is recorded and retains false return");
   ParalivesAccess.Speech.PrismNative.Result=ParalivesAccess.Speech.PrismNative.PrismError.Ok;ParalivesAccess.Speech.PrismNative.Throw=true;
   Check(!ParalivesAccess.Speech.ScreenReader.Speak("exception")&&Logged("nativeResult=exception:InvalidOperationException"),"native exception stays contained and is linked to the submission");ParalivesAccess.Speech.PrismNative.Throw=false;
   ParalivesAccess.Speech.ScreenReader.Stop();Check(ParalivesAccess.Speech.PrismNative.Stops==1&&Logged("phase=stop-request"),"explicit mod speech cancellation is visible");
   Plugin.Log.Lines.Clear();Time.unscaledTime=100;ParalivesAccess.Speech.ScreenReader.Speak("after audit");Check(Plugin.Log.Lines.Count==0,"speech tracing expires without changing native delivery");
   k.f3Key.wasPressedThisFrame=true;ContextDeliveryProbe.BeforeGameSystems();Check(Logged("source=frame-sample-without-event"),"frame-only F3 observations also arm the trace");k.f3Key.wasPressedThisFrame=false;
   ContextDeliveryProbe.Enabled=false;Plugin.Log.Lines.Clear();ParalivesAccess.Speech.ScreenReader.Speak("disabled");Check(Plugin.Log.Lines.Count==0,"disabled audit leaves ordinary speech uninstrumented");
   return count+" context-delivery adapter checks passed; actual F3 reception and audible playback require runtime evidence.";
  }
 }
}
'@
Add-Type -TypeDefinition ("using ParalivesAccess.Probes;`n" + $probe + "`n" + $adapters + "`n" + ($methods -join "`n") + "`n" + $checks) -Language CSharp
[ParalivesAccess.Probes.ContextChecks]::Run()
