$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$handler = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src\Handlers\SettingsHandler.cs')
# Exercise the production handler against explicit UI/input adapters. Native rendering, callbacks
# serialized into prefabs, and Prism audibility require the documented in-game checks.
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
 public class Transform:Component {public Transform parent;public int index;public int GetSiblingIndex()=>index;}
 public class RectTransform:Transform {public Rect rect;public Vector2 anchoredPosition;public Bounds bounds;}
 public struct Rect {public float yMin,yMax;}
 public struct Bounds {public Vector2 min,max;}
 public struct Vector2 {
  public float x,y;public Vector2(float a,float b){x=a;y=b;}public float sqrMagnitude=>x*x+y*y;
 }
 public static class Time {public static int frameCount;public static float unscaledTime;}
 public static class Mathf {public static float Abs(float v)=>Math.Abs(v);}
 public static class Canvas {public static void ForceUpdateCanvases(){}}
 public static class RectTransformUtility {public static Bounds CalculateRelativeRectTransformBounds(RectTransform v,RectTransform r)=>r.bounds;}
}
namespace UnityEngine.InputSystem {
 public class Delta {public UnityEngine.Vector2 value;public UnityEngine.Vector2 ReadValue()=>value;}
 public class Mouse {public static Mouse current=new Mouse();public Delta delta=new Delta();}
}
namespace UnityEngine.EventSystems {
 public enum MoveDirection {Left,Right,Up,Down}
 public class AxisEventData {public MoveDirection moveDir;public AxisEventData(EventSystem system){}}
 public class EventSystem {public static EventSystem current=new EventSystem();public UnityEngine.GameObject currentSelectedGameObject;public void SetSelectedGameObject(UnityEngine.GameObject go){currentSelectedGameObject=go;}}
}
namespace UnityEngine.UI {
 public class Event {
  public object target;public string method;public UnityEngine.Events.UnityEventCallState state=UnityEngine.Events.UnityEventCallState.RuntimeOnly;
  public List<Action<string>> listeners=new List<Action<string>>();
  public int GetPersistentEventCount()=>target==null?0:1;
  public object GetPersistentTarget(int i)=>target;public string GetPersistentMethodName(int i)=>method;
  public void AddListener(Action<string> a){listeners.Add(a);}public void RemoveListener(Action<string> a){listeners.Remove(a);}
  public void Invoke(string value){if(target!=null&&state!=UnityEngine.Events.UnityEventCallState.Off)target.GetType().GetMethod(method).Invoke(target,null);foreach(var a in listeners.ToArray())a(value);}
 }
 public class Slider:UnityEngine.Component {
  public Event onValueChanged=new Event();
  public enum Direction {LeftToRight,RightToLeft,BottomToTop,TopToBottom}
  public Direction direction;public bool interactable=true;public int moves;public UnityEngine.EventSystems.MoveDirection lastMove;
  public bool IsInteractable()=>interactable;
  public void OnMove(UnityEngine.EventSystems.AxisEventData e){moves++;lastMove=e.moveDir;}
 }
 public class Toggle:UnityEngine.Component {public bool isOn;}
 public class ScrollRect:UnityEngine.Component {public bool vertical;public UnityEngine.RectTransform content,viewport;public void StopMovement(){}}
}
namespace UnityEngine.Events {public enum UnityEventCallState {Off,EditorAndRuntime,RuntimeOnly}}
namespace TMPro {
 public class TextMeshProUGUI:UnityEngine.Component {public string text;}
 public class TMP_InputField:UnityEngine.Component {
  public UnityEngine.UI.Event onEndEdit=new UnityEngine.UI.Event();
  public string text;public bool readOnly,isFocused;public int caretPosition;
  public bool IsInteractable()=>true;
  public void Select(){UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(gameObject);}
  public void ActivateInputField(){isFocused=true;}
 }
}
namespace HarmonyLib {
 public class HarmonyMethod {public HarmonyMethod(Type t,string m){}}
 public class Harmony {private List<MethodBase> patched=new List<MethodBase>();public void Patch(MethodInfo m,HarmonyMethod postfix=null,HarmonyMethod prefix=null){patched.Add(m);}public IEnumerable<MethodBase> GetPatchedMethods()=>patched;}
 public static class AccessTools {
  public static FieldInfo Field(Type t,string n)=>t.GetField(n,BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Static|BindingFlags.Instance);
  public static MethodInfo Method(Type t,string n,Type[] types=null)=>types==null?t.GetMethod(n):t.GetMethod(n,types);
 }
}
public class Focusable:UnityEngine.Component {public bool IsShoulderButton;}
public class FocusableGroup:UnityEngine.Component {public Focusable focused;public Focusable GetFocused(bool shoulder)=>focused;}
public class ParaButton:Focusable {public UnityEngine.UI.Event ButtonClick=new UnityEngine.UI.Event();}
public class ParaToggle:ParaButton {public bool IsOn;}
public class UIListItemBase:ParaToggle {}
public class UIList {public List<UIListItemBase> CurrentItems=new List<UIListItemBase>();}
public class UIOptionsCategoryItem:UIListItemBase {public TranslatedText TranslatedTextCategoryName;}
public class UIWindow:FocusableGroup {public bool IsVisible=true;}
public class UIOptions:UIWindow {public UIInspector UIInspector;public UIList UIListOptionCategories;public void Update(){}}
public class UIDecalPlacement:UIWindow {public UIInspector Inspector;public void Update(){}}
public class UIStorytellerSelection:UIWindow {public UIInspector CustomStorytellerInspector;public void Update(){}}
public class UIClassicSliderItem:UIListItemBase {
 public TMPro.TMP_InputField InputField;public int commits;
 public void OnInputValueSet(){
  var t=typeof(ParalivesAccess.Handlers.SettingsHandler);
  if((bool)t.GetMethod("ClassicEditStarting",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{this}))commits++;
  t.GetMethod("ClassicEditEnded",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{this});
 }
}
public class UIInspector:UnityEngine.Component {public UnityEngine.UI.ScrollRect ScrollRect;}
public class UIInspectorComponentVisual {public UIInspector UIInspector;}
public class TranslatedText {public TMPro.TextMeshProUGUI Text=new TMPro.TextMeshProUGUI();}
public class UIFieldGameObject:UnityEngine.Component {
 public UIField UIField;public TranslatedText LabelFieldName=new TranslatedText();
 public TMPro.TMP_InputField InputField1,InputField2;public UnityEngine.UI.Slider Slider;public UnityEngine.UI.Toggle Toggle1;
 public ParaToggle ParaToggle1ForPlayersVersion;public ParaButton ButtonAction;public TMPro.TextMeshProUGUI ButtonActionLabel;
 public UnityEngine.RectTransform RectTransform;public void OnEndEdit(){}
}
public class UIField {
 public UIFieldGameObject UIFieldGameObject;public UIInspectorComponentVisual ComponentVisual;
 public bool HasDisableInspectorEditionAttribute;public string FieldName="Test";public Type FieldType;
 public List<object> ObjectHierarchy=new List<object>{new object()};public void Init(UIInspectorComponentVisual c){}
}
public class UIFieldSlider:UIField {public float MinValue,MaxValue;}
public class UIFieldInput:UIField {}
public class UIFieldHeader:UIField {}
public class UIFieldToggle:UIField {}
public class UIFieldKeyBinding:UIField {public UnityEngine.GameObject _imageWarningDuplicateBinding;}
public class KeyRebindingManagerPlayerData {public object RebindOperation;}
public static class KeyRebindingManager {public static Dictionary<int,KeyRebindingManagerPlayerData> KeyRebindingManagerDataByPlayer=new Dictionary<int,KeyRebindingManagerPlayerData>();}
public class Player {public int PlayerIndex;}
public class HybridPlayer {public Player Player=new Player();}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
namespace ParalivesAccess {
 public static class Loc {public static string Get(string key,params object[] args)=>key+":"+string.Join("|",args);}
 public class LogAdapter {public void LogInfo(object o){}public void LogWarning(object o){}public void LogError(object o){throw new Exception(o.ToString());}}
 public static class Plugin {public static LogAdapter Log=new LogAdapter();}
}
namespace ParalivesAccess.Speech {public static class ScreenReader {public static List<string> Sent=new List<string>();public static void Speak(string text){Sent.Add(text);}}}
namespace ParalivesAccess.Handlers {
 public static class ValueAdjustHandler {public static int reports;public static void ReportSliderValue(UIClassicSliderItem item){reports++;}}
 public static class WindowHandler {public static bool IsSceneTransitionActive;}
 public static class FocusNavigator {public static bool ShouldSkip(Focusable f)=>SettingsHandler.ShouldSkipSurface(f);}
 public static class SettingsChecks {
  static int checks;static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
  static void Hook(string name,object argument)=>typeof(SettingsHandler).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new[]{argument});
  static UIFieldGameObject Row(UIInspector owner,int index,string name,UIField field){
   var row=new UnityEngine.GameObject(owner.transform,index).AddComponent<UIFieldGameObject>();
   row.UIField=field;field.UIFieldGameObject=row;field.ComponentVisual=new UIInspectorComponentVisual{UIInspector=owner};
   row.LabelFieldName.Text.text=name;row.InputField1=new UnityEngine.GameObject(row.transform).AddComponent<TMPro.TMP_InputField>();row.InputField1.text="50";
   if(field is UIFieldSlider)row.Slider=new UnityEngine.GameObject(row.transform).AddComponent<UnityEngine.UI.Slider>();
   if(field.FieldType==typeof(UnityEngine.Vector2)){row.InputField2=new UnityEngine.GameObject(row.transform,1).AddComponent<TMPro.TMP_InputField>();row.InputField2.text="60";}
   Hook("FieldInitialized",field);return row;
  }
  public static string Run(){
   Check(SettingsHandler.ApplyPatches(new HarmonyLib.Harmony()),"all native hooks resolve");
   var window=new UnityEngine.GameObject().AddComponent<UIOptions>();
   var inspector=new UnityEngine.GameObject(window.transform).AddComponent<UIInspector>();window.UIInspector=inspector;
   var first=Row(inspector,0,"Master volume",new UIFieldSlider{MinValue=0,MaxValue=100});
   var middle=Row(inspector,1,"Music volume",new UIFieldSlider{MinValue=0,MaxValue=100});
   var last=Row(inspector,2,"Tooltip delay",new UIFieldSlider{MinValue=0,MaxValue=2});
   var a=first.GetComponent<Focusable>();var b=middle.GetComponent<Focusable>();var c=last.GetComponent<Focusable>();
   Check(a!=null&&b!=null&&c!=null,"previously unreachable native slider rows acquire focus stops");
   var list=new List<Focusable>{c,a,b,a};Focusable next;
   Check(SettingsHandler.TryNavigate(window,list,a,new UnityEngine.Vector2(0,-1),out next)&&next==b,"row order follows hierarchy despite shuffled duplicate registration");
   middle.gameObject.activeInHierarchy=false;
   SettingsHandler.TryNavigate(window,list,a,new UnityEngine.Vector2(0,-1),out next);Check(next==c,"hidden settings are skipped");
   SettingsHandler.TryNavigate(window,list,c,new UnityEngine.Vector2(0,-1),out next);Check(next==c,"bottom boundary cannot wrap into another setting");
   window.focused=c;Check(SettingsHandler.GetEntry(window,list)==c,"returning from a dropdown retains the previous setting");
   Check(!SettingsHandler.TryNavigate(new UnityEngine.GameObject().AddComponent<FocusableGroup>(),list,a,new UnityEngine.Vector2(0,-1),out next),"unrelated groups retain normal navigation");
   var other=new UnityEngine.GameObject().AddComponent<UIInspector>();var editorRow=Row(other,0,"Editor",new UIFieldSlider());
   Check(editorRow.GetComponent<Focusable>()==null&&SettingsHandler.GetLabel(editorRow)==null,"developer inspector is unaffected");
   Check(SettingsHandler.GetLabel(a).Contains("Master volume|50|0|100"),"slider speaks its label, native displayed value and range");
   ScreenReader.Sent.Clear();SettingsHandler.TryNavigate(window,list,a,new UnityEngine.Vector2(1,0),out next);
   Check(next==a&&first.Slider.moves==1&&first.Slider.lastMove==UnityEngine.EventSystems.MoveDirection.Right,"right delegates to native slider without moving focus");
   Check(ScreenReader.Sent.Count==0,"slider feedback waits for native display refresh");
   first.InputField1.text="63";Hook("OptionsRefreshed",window);
   Check(ScreenReader.Sent.Count==1&&ScreenReader.Sent[0]=="field_value:Master volume|63","feedback reads the native result rather than predicting a step");
   first.UIField.HasDisableInspectorEditionAttribute=true;SettingsHandler.TryNavigate(window,list,a,new UnityEngine.Vector2(-1,0),out next);
   Check(first.Slider.moves==1,"read-only slider is never written");first.UIField.HasDisableInspectorEditionAttribute=false;
   first.Slider.direction=UnityEngine.UI.Slider.Direction.TopToBottom;SettingsHandler.TryNavigate(window,list,a,new UnityEngine.Vector2(1,0),out next);
   Check(first.Slider.lastMove==UnityEngine.EventSystems.MoveDirection.Down,"reversed vertical slider uses its native increasing direction");Hook("OptionsRefreshed",window);
   var toggle=Row(inspector,3,"Autonomy",new UIFieldToggle{FieldType=typeof(bool)});
   toggle.ParaToggle1ForPlayersVersion=new UnityEngine.GameObject(toggle.transform).AddComponent<ParaToggle>();toggle.ParaToggle1ForPlayersVersion.IsOn=true;
   Check(SettingsHandler.GetLabel(toggle.ParaToggle1ForPlayersVersion)=="settings_toggle:Autonomy|settings_on:","checkbox reports its own row label and live state");
   var bind=Row(inspector,4,"Follow",new UIFieldKeyBinding());bind.ButtonAction=new UnityEngine.GameObject(bind.transform).AddComponent<ParaButton>();bind.ButtonActionLabel=new TMPro.TextMeshProUGUI{text="F"};
   var reset=new UnityEngine.GameObject(bind.transform,1).AddComponent<ParaButton>();
   var toggleShell=toggle.gameObject.AddComponent<Focusable>();
   var bindShell=bind.gameObject.AddComponent<Focusable>();
   var header=Row(inspector,5,"Header",new UIFieldHeader()).gameObject.AddComponent<Focusable>();
   Check(SettingsHandler.ShouldSkipSurface(toggleShell)&&SettingsHandler.ShouldSkipSurface(bindShell)&&SettingsHandler.ShouldSkipSurface(header),"inert native row shells and headers are skipped");
   Check(!SettingsHandler.ShouldSkipSurface(toggle.ParaToggle1ForPlayersVersion)&&!SettingsHandler.ShouldSkipSurface(bind.ButtonAction)&&!SettingsHandler.ShouldSkipSurface(reset)&&!SettingsHandler.ShouldSkipSurface(a),"real toggles, binding actions, reset and slider roots remain reachable");
   var category=new UnityEngine.GameObject(window.transform,0).AddComponent<UIOptionsCategoryItem>();category.IsOn=true;category.TranslatedTextCategoryName=new TranslatedText();category.TranslatedTextCategoryName.Text.text="Audio";
   window.UIListOptionCategories=new UIList();window.UIListOptionCategories.CurrentItems.Add(category);
   var close=new UnityEngine.GameObject(window.transform,1).AddComponent<ParaButton>();
   var nativeList=new List<Focusable>{close,header,bindShell,bind.ButtonAction,reset,toggleShell,toggle.ParaToggle1ForPlayersVersion,c,a,b,category};
   SettingsHandler.TryNavigate(window,nativeList,category,new UnityEngine.Vector2(1,0),out next);Check(next==a,"Right enters the displayed settings from categories");
   SettingsHandler.TryNavigate(window,nativeList,c,new UnityEngine.Vector2(0,-1),out next);Check(next==toggle.ParaToggle1ForPlayersVersion,"Down skips the inert toggle root to its working control");
   SettingsHandler.TryNavigate(window,nativeList,bind.ButtonAction,new UnityEngine.Vector2(1,0),out next);Check(next==reset,"Right reaches the per-binding reset within its row");
   SettingsHandler.TryNavigate(window,nativeList,reset,new UnityEngine.Vector2(0,-1),out next);Check(next==close,"window actions remain reachable after the last setting");
   SettingsHandler.TryNavigate(window,nativeList,toggle.ParaToggle1ForPlayersVersion,new UnityEngine.Vector2(-1,0),out next);Check(next==category,"Left from a non-slider returns to the current category");
   string section;SettingsHandler.TrySwitchSection(window,nativeList,category,1,out next,out section);Check(next==a&&section=="settings_category_values:Audio","panel key enters current category values in the same Options group");
   SettingsHandler.TrySwitchSection(window,nativeList,a,1,out next,out section);Check(next==close&&section=="settings_actions:","next section reaches Close and Reset");
   SettingsHandler.TrySwitchSection(window,nativeList,a,-1,out next,out section);Check(next==category&&section=="settings_categories:","previous section restores the active category");
   Check(SettingsHandler.GetLabel(bind.ButtonAction)=="settings_binding:Follow|F"&&SettingsHandler.GetLabel(reset)=="settings_reset_binding:Follow|F","binding and per-binding reset remain distinct");
   ((UIFieldKeyBinding)bind.UIField)._imageWarningDuplicateBinding=new UnityEngine.GameObject();
   Check(SettingsHandler.GetLabel(bind.ButtonAction).StartsWith("settings_binding_conflict:"),"native duplicate-binding warning is spoken");
   SettingsHandler.ActivateValueEntry(a);SettingsHandler.Tick();
   Check(SettingsHandler.OwnsEditInput&&UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject==first.InputField1.gameObject,"Enter selects the native numeric editor");
   first.InputField1.isFocused=false;first.InputField1.text="27";Hook("FieldEditEnded",first);SettingsHandler.Tick();
   Check(UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject==null&&SettingsHandler.OwnsEditInput,"completed edit clears selected field and consumes its submit frame");
   UnityEngine.Time.frameCount++;SettingsHandler.Tick();Check(!SettingsHandler.OwnsEditInput,"next frame returns to navigation");
   var data=new KeyRebindingManagerPlayerData{RebindOperation=new object()};KeyRebindingManager.KeyRebindingManagerDataByPlayer[0]=data;SettingsHandler.Tick();
   Check(SettingsHandler.IsCapturingBindingInput,"native rebind owns mod shortcuts");data.RebindOperation=null;SettingsHandler.Tick();
   Check(SettingsHandler.IsCapturingBindingInput,"binding completion frame cannot activate UI");UnityEngine.Time.frameCount++;SettingsHandler.Tick();
   Check(!SettingsHandler.IsCapturingBindingInput,"mod shortcuts resume on the following frame");
   inspector.ScrollRect=new UnityEngine.UI.ScrollRect{vertical=true,content=new UnityEngine.RectTransform(),viewport=new UnityEngine.RectTransform{rect=new UnityEngine.Rect{yMin=0,yMax=100}}};
   first.RectTransform=new UnityEngine.RectTransform{bounds=new UnityEngine.Bounds{min=new UnityEngine.Vector2(0,-30),max=new UnityEngine.Vector2(0,-10)}};
   UnityEngine.Time.unscaledTime=1;SettingsHandler.OnFocusGained(a);
   Check(inspector.ScrollRect.content.anchoredPosition.y==30,"keyboard focus reveals an off-screen settings row");
   Check(SettingsHandler.ShouldSuppressScrollHover(a),"scroll-generated stationary-pointer hover is suppressed");
   UnityEngine.InputSystem.Mouse.current.delta.value=new UnityEngine.Vector2(1,0);Check(!SettingsHandler.ShouldSuppressScrollHover(a),"real pointer movement remains available");

   var tattoo=new UnityEngine.GameObject().AddComponent<UIDecalPlacement>();
   tattoo.Inspector=new UnityEngine.GameObject(tattoo.transform).AddComponent<UIInspector>();
   var rotation=Row(tattoo.Inspector,0,"Rotation",new UIFieldSlider{MinValue=-180,MaxValue=180});var rotationFocus=rotation.GetComponent<Focusable>();
   Check(rotationFocus!=null&&SettingsHandler.GetLabel(rotationFocus).Contains("Rotation|50|-180|180"),"tattoo rotation gains a labeled focus stop with its native range");
   Check(SettingsHandler.TryAdjustValue(rotationFocus,true)&&rotation.Slider.moves==1,"tattoo 7/8 delegates to the native slider callback route");
   ScreenReader.Sent.Clear();Hook("OptionsRefreshed",window);Check(ScreenReader.Sent.Count==0,"unrelated window refresh cannot consume tattoo feedback");
   rotation.InputField1.text="54";Hook("InspectorRefreshed",tattoo);Check(ScreenReader.Sent.Count==1&&ScreenReader.Sent[0]=="field_value:Rotation|54","tattoo feedback reads its own native owner refresh");
   Check(SettingsHandler.TryNavigate(tattoo,new List<Focusable>{rotationFocus},rotationFocus,new UnityEngine.Vector2(-1,0),out next)&&next==rotationFocus&&rotation.Slider.moves==2,"tattoo arrows adjust without losing focus");
   Check(!SettingsHandler.TryNavigate(window,list,rotationFocus,new UnityEngine.Vector2(-1,0),out next)||next!=rotationFocus,"stale tattoo cursor cannot adjust through an unrelated group");
   Check(rotation.Slider.moves==2,"unrelated group never writes tattoo slider");
   Check(!SettingsHandler.TryNavigate(tattoo,new List<Focusable>{rotationFocus},rotationFocus,new UnityEngine.Vector2(0,-1),out next),"tattoo vertical traversal remains with shared logical navigation");
   tattoo.IsVisible=false;Check(!SettingsHandler.TryAdjustValue(rotationFocus,true),"hidden tattoo window cannot be adjusted");tattoo.IsVisible=true;
   var position=Row(tattoo.Inspector,1,"Position",new UIFieldInput{FieldType=typeof(UnityEngine.Vector2)});
   var x=position.InputField1.GetComponent<Focusable>();var y=position.InputField2.GetComponent<Focusable>();
   Check(x!=null&&y!=null&&SettingsHandler.IsValueEntryTarget(x)&&SettingsHandler.IsValueEntryTarget(y),"tattoo position exposes both native coordinate editors");
   Check(SettingsHandler.GetLabel(x)=="field_value:settings_axis_x:Position|50"&&SettingsHandler.GetLabel(y)=="field_value:settings_axis_y:Position|60","coordinate names and values remain distinct");
   SettingsHandler.ActivateValueEntry(y);SettingsHandler.Tick();Check(UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject==position.InputField2.gameObject,"Enter on Y opens Y rather than X");
   position.InputField2.text="75";position.InputField2.isFocused=false;Hook("FieldEditEnded",position);SettingsHandler.Tick();ScreenReader.Sent.Clear();Hook("InspectorRefreshed",tattoo);
   Check(ScreenReader.Sent.Count==1&&ScreenReader.Sent[0]=="field_value:settings_axis_y:Position|75","coordinate result reads the edited component after native refresh");UnityEngine.Time.frameCount++;
   var story=new UnityEngine.GameObject().AddComponent<UIStorytellerSelection>();story.CustomStorytellerInspector=new UnityEngine.GameObject(story.transform).AddComponent<UIInspector>();
   var frequency=Row(story.CustomStorytellerInspector,0,"Frequency",new UIFieldSlider{MinValue=0,MaxValue=3});var frequencyFocus=frequency.GetComponent<Focusable>();
   Check(frequencyFocus!=null&&SettingsHandler.TryNavigate(story,new List<Focusable>{frequencyFocus},frequencyFocus,new UnityEngine.Vector2(1,0),out next),"custom storyteller inspector supports native slider adjustment");
   ScreenReader.Sent.Clear();frequency.InputField1.text="1.25";Hook("InspectorRefreshed",story);Check(ScreenReader.Sent[0]=="field_value:Frequency|1.25","custom storyteller reports its actual refreshed value");
   var unrelatedInspector=new UnityEngine.GameObject(story.transform).AddComponent<UIInspector>();
   Check(Row(unrelatedInspector,0,"Unrelated",new UIFieldSlider()).GetComponent<Focusable>()==null,"only the explicitly owned custom inspector is bridged");
   SettingsHandler.ActivateValueEntry(frequencyFocus);SettingsHandler.Tick();Check(SettingsHandler.OwnsEditInput,"custom storyteller opens native numeric entry");
   frequency.InputField1.isFocused=false;Hook("FieldEditEnded",frequency);SettingsHandler.Tick();UnityEngine.Time.frameCount++;

   var classic=new UnityEngine.GameObject().AddComponent<UIClassicSliderItem>();classic.InputField=new UnityEngine.GameObject(classic.transform).AddComponent<TMPro.TMP_InputField>();classic.InputField.text="50";
   Check(SettingsHandler.IsValueEntryTarget(classic),"body slider exposes its existing numeric entry");
   SettingsHandler.ActivateValueEntry(classic);SettingsHandler.Tick();
   Check(SettingsHandler.OwnsEditInput&&classic.InputField.onEndEdit.listeners.Count==1,"missing prefab commit is connected once while editing owns input");
   classic.InputField.text="37";classic.InputField.onEndEdit.Invoke("37");classic.InputField.isFocused=false;SettingsHandler.Tick();
   Check(classic.commits==1&&ValueAdjustHandler.reports==1,"typed body value commits once and reports native result");
   Check(classic.InputField.onEndEdit.listeners.Count==0,"completed edit releases its temporary callback so another field cannot trigger it");
   Check(SettingsHandler.OwnsEditInput,"body numeric submit frame remains protected");UnityEngine.Time.frameCount++;SettingsHandler.Tick();Check(!SettingsHandler.OwnsEditInput,"body numeric navigation resumes on the next frame");
   classic.InputField.onEndEdit.target=classic;classic.InputField.onEndEdit.method="OnInputValueSet";
   SettingsHandler.ActivateValueEntry(classic);SettingsHandler.Tick();Check(classic.InputField.onEndEdit.listeners.Count==1,"existing native wiring retains one temporary completion observer");
   classic.InputField.text="65";classic.InputField.onEndEdit.Invoke("65");classic.InputField.onEndEdit.Invoke("65");classic.InputField.isFocused=false;SettingsHandler.Tick();
   Check(classic.commits==2&&ValueAdjustHandler.reports==2,"duplicate end-edit in one edit cannot add another undo entry");UnityEngine.Time.frameCount++;
   foreach(string invalid in new[]{"","bad","101","-1","NaN"}){
    SettingsHandler.ActivateValueEntry(classic);SettingsHandler.Tick();ScreenReader.Sent.Clear();classic.InputField.text=invalid;classic.InputField.onEndEdit.Invoke(invalid);classic.InputField.isFocused=false;SettingsHandler.Tick();UnityEngine.Time.frameCount++;
    Check(classic.commits==2&&ValueAdjustHandler.reports==2&&ScreenReader.Sent.Count==1&&ScreenReader.Sent[0].StartsWith("slider_invalid_value:"),"invalid body value is refused without a write or misleading success: "+invalid);
   }
   classic.InputField.onEndEdit.state=UnityEngine.Events.UnityEventCallState.Off;SettingsHandler.ActivateValueEntry(classic);SettingsHandler.Tick();
   Check(classic.InputField.onEndEdit.listeners.Count==1,"disabled native listener is treated as unwired");classic.InputField.text="28";classic.InputField.onEndEdit.Invoke("28");classic.InputField.isFocused=false;SettingsHandler.Tick();UnityEngine.Time.frameCount++;
   Check(classic.commits==3,"disabled prefab listener still permits one native numeric commit");
   return checks+" settings checks passed; native prefab wiring and audibility remain pending.";
  }
 }
}
'@
Add-Type -TypeDefinition ($handler + "`n" + $adapters) -Language CSharp
[ParalivesAccess.Handlers.SettingsChecks]::Run()
