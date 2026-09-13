param([string]$RuntimeLog)
$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\ContextKeyInput.cs')
# Compile the complete production event reader. Frame-edge flags deliberately remain false:
# this reproduces the installed Input System's same-update down/up failure, not a new keyboard API.
$adapters = @'
namespace UnityEngine {
 public static class Time {public static int frameCount;}
 public static class Application {public static bool isFocused=true;}
}
namespace UnityEngine.InputSystem.LowLevel {
 public struct StateEvent {} public struct DeltaStateEvent {}
 public struct InputEventPtr {
  public Type Kind;public float? F3,LeftShift,RightShift;public bool handled;
  public bool IsA<T>()=>Kind==typeof(T);
 }
}
namespace UnityEngine.InputSystem.Controls {
 public class ButtonControl {
  public int Slot;public bool isPressed,wasPressedThisFrame;
  public bool ReadValueFromEvent(UnityEngine.InputSystem.LowLevel.InputEventPtr e,out float value){float? v=Slot==0?e.F3:Slot==1?e.LeftShift:e.RightShift;value=v??0;return v.HasValue;}
  public bool IsValueConsideredPressed(float v)=>v>=0.5f;
 }
}
namespace UnityEngine.InputSystem {
 public class InputDevice {public int deviceId;}
 public class Keyboard:InputDevice {
  public static Keyboard current;
  public Controls.ButtonControl f3Key=new Controls.ButtonControl(),leftShiftKey=new Controls.ButtonControl{Slot=1},rightShiftKey=new Controls.ButtonControl{Slot=2};
 }
 public static class InputSystem {
  public static event Action<LowLevel.InputEventPtr,InputDevice> onEvent;
  public static int Subscribers=>onEvent?.GetInvocationList().Length??0;
  public static void Send(LowLevel.InputEventPtr e,InputDevice device){
   onEvent?.Invoke(e,device);
   var k=device as Keyboard;if(k==null)return;
   if(e.F3.HasValue)k.f3Key.isPressed=e.F3.Value>=0.5f;
   if(e.LeftShift.HasValue)k.leftShiftKey.isPressed=e.LeftShift.Value>=0.5f;
   if(e.RightShift.HasValue)k.rightShiftKey.isPressed=e.RightShift.Value>=0.5f;
  }
 }
}
namespace ParalivesAccess {
 public class Logger {public void LogInfo(string s){}public void LogError(string s){throw new Exception(s);}}
 public static class Plugin {public static Logger Log=new Logger();}
}
namespace ParalivesAccess.Handlers {
 public static class SettingsHandler {public static bool IsCapturingBindingInput;}
 public static class ContextKeyChecks {
  static int checks;
  static void Check(bool pass,string name){if(!pass)throw new Exception(name);checks++;}
  static void Event(Keyboard k,float? f3,float? left=null,float? right=null,bool handled=false,bool delta=false){InputSystem.Send(new InputEventPtr{Kind=delta?typeof(DeltaStateEvent):typeof(StateEvent),F3=f3,LeftShift=left,RightShift=right,handled=handled},k);}
  static void Sample(){UnityEngine.Time.frameCount++;ContextKeyInput.BeforeGameSystems();}
  public static string Run(string log){
   var k=new Keyboard{deviceId=1};Keyboard.current=k;
   ContextKeyInput.Install();ContextKeyInput.Install();Check(InputSystem.Subscribers==1,"one listener after repeated installation");
   Event(k,1);Event(k,0);Sample();
   Check(ContextKeyInput.ContextPressed&&!ContextKeyInput.RepeatPressed&&!k.f3Key.wasPressedThisFrame,"same-update tap survives absent frame-edge flags");
   ContextKeyInput.BeforeGameSystems();Check(ContextKeyInput.ContextPressed,"both consumers see the same frame snapshot");
   Sample();Check(!ContextKeyInput.ContextPressed,"request is not repeated next frame");
   Event(k,1);Sample();Check(ContextKeyInput.ContextPressed,"ordinary key-down is captured");
   Event(k,1);Sample();Check(!ContextKeyInput.ContextPressed,"held state does not autorepeat context");Event(k,0);Sample();
   Event(k,1,1);Event(k,0,0);Sample();Check(!ContextKeyInput.ContextPressed&&ContextKeyInput.RepeatPressed,"same-update Shift+F3 retains down-event modifier");
   Event(k,1,0,1);Event(k,0,0,0);Sample();Check(ContextKeyInput.RepeatPressed&&!ContextKeyInput.ContextPressed,"right Shift also selects important-event repeat");
   Event(k,null,1);Event(k,1,delta:true);Event(k,0,0);Sample();Check(ContextKeyInput.RepeatPressed,"partial F3 event uses prior modifier state");
   Event(k,1,0);Event(k,0,1);Sample();Check(ContextKeyInput.ContextPressed&&!ContextKeyInput.RepeatPressed,"Shift pressed after F3 does not change the request");Event(k,null,0);
   Event(k,null);Sample();Check(!ContextKeyInput.ContextPressed&&!ContextKeyInput.RepeatPressed,"partial event without F3 produces no request");
   Event(k,1,handled:true);Event(k,0);Sample();Check(!ContextKeyInput.ContextPressed,"handled input is not stolen");
   SettingsHandler.IsCapturingBindingInput=true;Event(k,1);Event(k,0);SettingsHandler.IsCapturingBindingInput=false;Sample();Check(!ContextKeyInput.ContextPressed,"binding-capture taps do not escape after capture closes");
   Event(k,1);Event(k,0);SettingsHandler.IsCapturingBindingInput=true;Sample();Check(!ContextKeyInput.ContextPressed,"pending request is discarded when rebinding begins");SettingsHandler.IsCapturingBindingInput=false;
   UnityEngine.Application.isFocused=false;Event(k,1);Event(k,0);UnityEngine.Application.isFocused=true;Sample();Check(!ContextKeyInput.ContextPressed,"background input does not leak on refocus");
   Event(k,1);Event(k,0);UnityEngine.Application.isFocused=false;Sample();UnityEngine.Application.isFocused=true;Sample();Check(!ContextKeyInput.ContextPressed,"focus loss discards a pending request");
   Event(k,1);Sample();var k2=new Keyboard{deviceId=2};Event(k2,1);Event(k2,0);Sample();Check(ContextKeyInput.ContextPressed,"independent second keyboard works while first holds F3");Event(k,0);Sample();
   Event(k,1);Event(k,0);Event(k,1,1);Event(k,0,0);Sample();Check(ContextKeyInput.ContextPressed&&ContextKeyInput.RepeatPressed,"different request kinds in one update both survive");
   Event(k,1);Event(k,0);Event(k,1);Event(k,0);Sample();Check(ContextKeyInput.ContextPressed,"same-frame equivalent requests coalesce into one context report");Sample();
   int captured=0,downEvents=0;
   if(!string.IsNullOrEmpty(log)){
    var replay=new Keyboard{deviceId=99};Keyboard.current=replay;int frame=-1;
    foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(log,@"\[ContextDelivery\].*F3=(down|up),.*frame=(\d+),")){
     int next=int.Parse(match.Groups[2].Value);
     if(frame!=-1&&next!=frame){ContextKeyInput.BeforeGameSystems();if(ContextKeyInput.ContextPressed)captured++;}
     frame=next;UnityEngine.Time.frameCount=frame;
     bool down=match.Groups[1].Value=="down";if(down)downEvents++;Event(replay,down?1:0);
    }
    ContextKeyInput.BeforeGameSystems();if(ContextKeyInput.ContextPressed)captured++;
    Check(downEvents>0&&captured==downEvents,"every recorded runtime F3 down yields a context request");
    Sample();Check(!ContextKeyInput.ContextPressed,"replayed requests do not leak after the last frame");
   }
   return checks+" context-key checks passed; runtime replay captured "+captured+" of "+downEvents+" presses.";
  }
 }
}
'@
Add-Type -TypeDefinition ($source + "`n" + $adapters) -Language CSharp
$trace = if ($RuntimeLog) { Get-Content -Raw -LiteralPath $RuntimeLog } else { '' }
[ParalivesAccess.Handlers.ContextKeyChecks]::Run($trace)
