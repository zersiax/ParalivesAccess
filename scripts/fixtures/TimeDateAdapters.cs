
public enum State {MainMenu,Loading,Game}
public static class GameLoadingManager {public static State State=State.Game;}
public class SavedGameManager {public static SavedGameManager Instance=new SavedGameManager();public bool HasLoadedSavedGame=true;}
public class Player {public object Focus,Preview,Panel;public bool Blocked;public int Money=123,Speed=2;}
public class HybridPlayer {public Player Player=new Player();public bool HasInputFieldFocused;}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public static class ParaTime {public static double TotalMinutes=2027.123456789;}
public static class Settings {public static Setting.GeneralOptions Options=new Setting.GeneralOptions();public static T Get<T>() where T:class=>Options as T;}
namespace Setting {public class GeneralOptions {public bool Hour24Mode=true;}}
namespace UnityEngine {public static class Application {public static bool isFocused=true;}}
namespace UnityEngine.InputSystem {public enum Key {None,J,R,F1=20,F2,F3,F4,F5,F6,F7,F8,F9,F10,F11,F12}}
public static class FormatService {
 public static double TimeArgument,DateArgument;public static bool Hour24,DayMonth,Year,MutateClock,Throw;public static string TimeText="9:47",DateText="Tuesday, January 2, 2026";
 public static string GetTimeOfDay(double minutes,bool hour24Mode){TimeArgument=minutes;Hour24=hour24Mode;if(Throw)throw new Exception("test formatter failure");if(MutateClock)ParaTime.TotalMinutes+=1440;return TimeText;}
 public static string GetDate(double minutes,bool includeDayAndMonth,bool includeYear){DateArgument=minutes;DayMonth=includeDayAndMonth;Year=includeYear;return DateText;}
}
namespace ParalivesAccess.Speech {
 public static class ScreenReader {
  public static readonly System.Collections.Generic.List<string> Output=new System.Collections.Generic.List<string>();
  public static string NormalizeForOutput(string text)=>text==null?null:System.Text.RegularExpressions.Regex.Replace(text,"<[^>]+>","").Trim();
  public static void Speak(string text){Output.Add(text);}
 }
}
namespace ParalivesAccess {
 public static class Plugin {public static Logger Log=new Logger();}
 public class Logger {public void LogInfo(string s){}public void LogError(string s){}}
 public static class Loc {public static string Get(string k,params object[] p)=>k=="time_date_report"?p[0]+". "+p[1]+".":"Time/date unavailable";}
}
namespace ParalivesAccess.Handlers {
 public static class ContextKeyInput {public static bool Pending;public static bool TakeTimeDateRequest(){bool p=Pending;Pending=false;return p;}}
 public static class SettingsHandler {public static bool IsCapturingBindingInput;}
 public static class ModInputKey {public static bool Available=true;public static int Checks;public static bool IsAvailable(HybridPlayer h,Key key,out string conflict){Checks++;conflict="Binding conflict";return Available;}}
 public static class BuildCatalogHandler {public static Key OpenKey=Key.F6;}
 public static class LotObjectHandler {public static Key OpenKey=Key.F7;}
 public static class ContextActionHandler {public static Key ActionKey=Key.F4;}
 public static class VirtualAudioListenerHandler {public static Key ToggleKey=Key.F8;}
 public static class ParaSwitchHandler {public static Key SwitchKey=Key.F9;}
 public static class WallConstructionHandler {public static Key RectangleKey=Key.R;}
 public static class LotDescriptionHandler {public static Key DescribeKey=Key.J;}
}
public static class TimeDateChecks {
 static int count;
 static void Check(bool value,string name){count++;if(!value)throw new Exception(name);}
 static void Press(){ParalivesAccess.Handlers.ContextKeyInput.Pending=true;ParalivesAccess.Handlers.TimeDateHandler.Tick();}
 public static void Run(){
  var hybrid=PlayerManager.Instance.HybridPlayer1;var player=hybrid.Player;
  ParalivesAccess.Handlers.TimeDateHandler.ConfigureKey(Key.F2);
  Check(ParalivesAccess.Handlers.TimeDateHandler.ReadKey==Key.F2,"default available function key");
  Press();Check(ParalivesAccess.Speech.ScreenReader.Output.Count==1,"one response to a request");
  Check(FormatService.TimeArgument==ParaTime.TotalMinutes%1440d&&FormatService.DateArgument==ParaTime.TotalMinutes,"native double precision snapshot retained");
  Check(FormatService.Hour24&&FormatService.DayMonth&&FormatService.Year,"native hour option and complete date requested");
  ParalivesAccess.Handlers.TimeDateHandler.Tick();Check(ParalivesAccess.Speech.ScreenReader.Output.Count==1,"request consumed before another tick");
  Press();Check(ParalivesAccess.Speech.ScreenReader.Output.Count==2,"identical paused reading repeats");
  Settings.Options.Hour24Mode=false;FormatService.TimeText="9:47<size=65%> am</size>";Press();
  Check(!FormatService.Hour24&&ParalivesAccess.Speech.ScreenReader.Output[2].StartsWith("9:47 am.")&&!ParalivesAccess.Speech.ScreenReader.Output[2].Contains("<"),"12 hour native text normalized");
  double snapshot=ParaTime.TotalMinutes;FormatService.MutateClock=true;Press();
  Check(FormatService.DateArgument==snapshot,"formatter cannot split midnight snapshot");FormatService.MutateClock=false;ParaTime.TotalMinutes=snapshot;
  player.Focus=new object();player.Preview=new object();player.Panel=new object();hybrid.HasInputFieldFocused=true;player.Blocked=true;
  object focus=player.Focus,preview=player.Preview,panel=player.Panel;Press();
  Check(ParalivesAccess.Speech.ScreenReader.Output.Count==5,"clock available during text entry and exclusive/modal activity");
  Check(player.Focus==focus&&player.Preview==preview&&player.Panel==panel&&player.Money==123&&player.Speed==2,"observer preserves focus preview menu balance and speed");
  int before=ParalivesAccess.Speech.ScreenReader.Output.Count;UnityEngine.Application.isFocused=false;Press();UnityEngine.Application.isFocused=true;ParalivesAccess.Handlers.TimeDateHandler.Tick();
  Check(ParalivesAccess.Speech.ScreenReader.Output.Count==before,"focus loss consumes request without deferred speech");
  ParalivesAccess.Handlers.SettingsHandler.IsCapturingBindingInput=true;Press();ParalivesAccess.Handlers.SettingsHandler.IsCapturingBindingInput=false;ParalivesAccess.Handlers.TimeDateHandler.Tick();
  Check(ParalivesAccess.Speech.ScreenReader.Output.Count==before,"rebinding consumes request without deferred speech");
  ParalivesAccess.Handlers.ModInputKey.Available=false;Press();Check(ParalivesAccess.Speech.ScreenReader.Output[before]=="Binding conflict","native binding conflict replaces clock reading");ParalivesAccess.Handlers.ModInputKey.Available=true;
  GameLoadingManager.State=State.MainMenu;Press();Check(ParalivesAccess.Speech.ScreenReader.Output[before+1]=="Time/date unavailable","main menu never reports stale simulation time");
  GameLoadingManager.State=State.Loading;Press();Check(ParalivesAccess.Speech.ScreenReader.Output[before+2]=="Time/date unavailable","loading never reports old save clock");GameLoadingManager.State=State.Game;
  SavedGameManager.Instance.HasLoadedSavedGame=false;Press();Check(ParalivesAccess.Speech.ScreenReader.Output[before+3]=="Time/date unavailable","unloaded session unavailable");SavedGameManager.Instance.HasLoadedSavedGame=true;
  PlayerManager.Instance.HybridPlayer1=null;Press();Check(ParalivesAccess.Speech.ScreenReader.Output[before+4]=="Time/date unavailable","missing player unavailable");PlayerManager.Instance.HybridPlayer1=hybrid;
  foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,-1d}){ParaTime.TotalMinutes=invalid;Check(ParalivesAccess.Handlers.TimeDateHandler.Describe()==null,"invalid clock not formatted");}ParaTime.TotalMinutes=snapshot;
  Settings.Options=null;Check(ParalivesAccess.Handlers.TimeDateHandler.Describe()==null,"missing native options");Settings.Options=new Setting.GeneralOptions();
  FormatService.TimeText=" ";Check(ParalivesAccess.Handlers.TimeDateHandler.Describe()==null,"empty native time");FormatService.TimeText="9:47";FormatService.DateText=null;
  Check(ParalivesAccess.Handlers.TimeDateHandler.Describe()==null,"empty native date");FormatService.DateText="Tuesday, January 2, 2026";
  FormatService.Throw=true;Press();Check(ParalivesAccess.Speech.ScreenReader.Output[ParalivesAccess.Speech.ScreenReader.Output.Count-1]=="Time/date unavailable","formatter failure announced without disabling future requests");FormatService.Throw=false;Press();
  Check(ParalivesAccess.Speech.ScreenReader.Output[ParalivesAccess.Speech.ScreenReader.Output.Count-1].StartsWith("9:47."),"recovery after formatter failure");
  foreach(Key key in new[]{Key.J,Key.F3,Key.F4,Key.F6,Key.F7,Key.F8,Key.F9,Key.F10,Key.F11}){ParalivesAccess.Handlers.TimeDateHandler.ConfigureKey(key);Check(ParalivesAccess.Handlers.TimeDateHandler.ReadKey==Key.None,"unsafe or occupied key rejected: "+key);}
  ParalivesAccess.Handlers.BuildCatalogHandler.OpenKey=Key.F2;ParalivesAccess.Handlers.TimeDateHandler.ConfigureKey(Key.F2);Check(ParalivesAccess.Handlers.TimeDateHandler.ReadKey==Key.None,"custom accessibility binding wins");ParalivesAccess.Handlers.BuildCatalogHandler.OpenKey=Key.F6;
  ParalivesAccess.Handlers.TimeDateHandler.ConfigureKey(Key.F1);Check(ParalivesAccess.Handlers.TimeDateHandler.ReadKey==Key.F1,"unused configured function key accepted");
  ParalivesAccess.Handlers.TimeDateHandler.ConfigureKey(Key.None);before=ParalivesAccess.Speech.ScreenReader.Output.Count;Press();Check(ParalivesAccess.Speech.ScreenReader.Output.Count==before,"disabled shortcut stays quiet");
  Console.WriteLine(count+" time/date observer checks passed; formatting adapters do not establish native calendar or speech behavior.");
 }
}
