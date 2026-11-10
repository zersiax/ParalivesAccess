$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sources = @('CatalogEntryReader','FloorCoveringHandler','BuildCatalogHandler','VirtualPanel','LotBuildInputOwnership') | ForEach-Object {
    (Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot "src/Handlers/$_.cs")) -replace '(?m)^using [^;]+;\r?\n', ''
}
$labels = Get-Content -Raw -Encoding UTF8 (Join-Path $projectRoot 'src/Handlers/UiLabelReader.cs')
$start = $labels.IndexOf('private static string GetBuildModeItemLabel(')
if ($start -lt 0) { throw 'Production catalogue label method missing' }
$body = $labels.IndexOf('{', $start); $depth = 1; $end = $body + 1
while ($depth -gt 0) { if ($labels[$end] -eq '{') { $depth++ }; if ($labels[$end] -eq '}') { $depth-- }; $end++ }
$labelMethod = $labels.Substring($start, $end - $start)
# Full production workflows, with explicit native observations/call-result adapters. No simulated
# renderer, game funds, undo stack or save system is presented as native runtime verification.
$adapters = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using Setting;
using ParalivesAccess.Speech;
namespace UnityEngine {
 public static class Time {public static int frameCount;}
 public class GameObject {public bool activeInHierarchy=true,activeSelf=true;}
 public struct Vector3 {public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}}
 public struct Color {public float r,g,b,a;public Color(float r,float g,float b,float a=1){this.r=r;this.g=g;this.b=b;this.a=a;}}
}
namespace UnityEngine.InputSystem {
 public enum Key {F6,Home}
 public class Control {public bool wasPressedThisFrame,wasReleasedThisFrame,isPressed;}
 public class Keyboard {public static Keyboard current=new Keyboard();public Key? Pressed;public Control downArrowKey=new Control(),rightArrowKey=new Control(),upArrowKey=new Control(),leftArrowKey=new Control(),enterKey=new Control(),numpadEnterKey=new Control(),homeKey=new Control(),ctrlKey=new Control(),altKey=new Control(),shiftKey=new Control();}
}
namespace Setting {
 public class Surface {public ulong GUID;public string DisplayName;public string TranslationKey=>"Surface_"+DisplayName;}
 public class Surfaces {public Dictionary<ulong,Surface> Data=new Dictionary<ulong,Surface>();public Surface GetSurfaceByGUID(ulong id)=>Data.TryGetValue(id,out var s)?s:null;}
 public class Wall {public float StepHeight=0.1f;}
}
public static class Settings {static Dictionary<Type,object> all=new Dictionary<Type,object>();public static T Get<T>()where T:new(){if(!all.ContainsKey(typeof(T)))all[typeof(T)]=new T();return (T)all[typeof(T)];}}
public static class TranslationManager {public static string Get(string key)=>"translated "+key;}
public static class FormatService {public static string GetMoney(int n)=>"MONEY "+n;public static string AddSpacesBetweenUpperCamelCaseWords(string text,bool onlyCapitalizeFirstLetter)=>System.Text.RegularExpressions.Regex.Replace(text,"([a-z0-9])([A-Z])","$1 $2");}
public static class MeasurementUnitService {public static string FormatSurfaceArea(float area)=>area+" square metres";}
public class CatalogueItem {public ulong GUID;}
public enum UIBuildModeItemTypes {None,Items,Surfaces,Fences,Moldings,TerrainPaint}
public class Focusable {public string name="panel";public GameObject gameObject=new GameObject();public bool Interactable=true;}
public class UIListItemBase:Focusable {}
public class UIBuildModeItem:UIListItemBase {
 private UIBuildModeItemTypes _type; private Surface _surface;
 public CatalogueItem ItemData;public int Price=9999,VariantIndex;public ulong ItemSkin;
 public bool Selects=true;public PaintCategory Category=PaintCategory.Floors;public UIBuildModeItemTypes ReadType=>_type;public Surface ReadSurface=>_surface;
 public void Bind(UIBuildModeItemTypes type,Surface surface=null){_type=type;_surface=surface;}
 public void Select(){if(Selects&&_type==UIBuildModeItemTypes.Surfaces){var p=PlayerManager.Instance.HybridPlayer1.Player;p.State=GameStates.Painting;p.PaintCategory=Category;p.SelectedSurface=_surface.GUID;p.SelectedColors=new[]{new Color(1,0,0)};}}
}
public class UIGenericCategoriesData {public ulong Tag;public bool IsOn;}
public class UIGenericCategoriesButton:UIListItemBase {public UIGenericCategoriesData Data;}
public class UIList {public List<UIListItemBase> CurrentItems=new List<UIListItemBase>();}
public class CategoryList {public GameObject GameObjectToShow=new GameObject();public UIList UIList=new UIList();}
public class UIGenericCategories {public CategoryList[] CategoryLists=new CategoryList[0];}
public class UIBuildModeItemList {public UIList UIList=new UIList();}
public class UIManager {public object CurrentWindow;}
public class UIBuildModeCatalog {public bool IsVisible=true,RegisterWindowForEscapeButtonEvent,BlockPlayerActionWhenShown;public UIGenericCategories GenericUI=new UIGenericCategories();public UIBuildModeItemList ItemList=new UIBuildModeItemList();public UIManager UIManager=new UIManager();}
public class UI {public static UI Instance=new UI();public static UIBuildModeCatalog Catalog=new UIBuildModeCatalog();public static T GetOrNull<T>(int id) where T:class=>Catalog as T;}
public enum GameStates {BuildMode,Painting,PlaceSegment,LiveMode}
public enum PaintCategory {Floors,Walls,Ceilings}
public enum SegmentTypes {Wall}
public class Button {public bool Down;}
public class Player {public bool FreezeCameraMovement,PlayerActionsAreAllowed=true,PlayerActionBlockedByUIWindow,BuildModeCatalogueListDirty,CatalogueOpened;public int PlayerIndex,Layer;public GameStates State=GameStates.Painting;public PaintCategory PaintCategory=PaintCategory.Floors;public SegmentTypes CurrentSegmentType;public object ItemInPlacement,SegmentInPlacement,PlatformInPlacement;public ulong SelectedSurface=2,TownMapForFastTravelMethod;public Color[] SelectedColors=new[]{new Color(1,0,0)};public ZoneWallPlatformObject PlatformHovered;public bool PlatformHoveredIsUnderside;public int GetLotLayer(ulong lot)=>Layer;}
public class HybridPlayer {public Player Player=new Player();public bool HasInputFieldFocused,IsUsingGamePad,TownMap;public bool IsInTownMap(bool checkForHouseholdStatus=true)=>TownMap;public Button ButtonCancel=new Button(),ButtonUndo=new Button(),ButtonRedo=new Button(),ButtonConfirm=new Button();}
public class PlayerManager {public static PlayerManager Instance=new PlayerManager();public HybridPlayer HybridPlayer1=new HybridPlayer();}
public class SegmentObject {public List<Vector3> SegmentPoints=new List<Vector3>{new Vector3(0,0,0),new Vector3(2,0,0)};}
public class ZoneSegment {public SegmentObject Segment=new SegmentObject();}
public class ZoneObject {public GameObject gameObject=new GameObject();public int InstanceID,Layer;public ulong LotPlacedOnGUID=1;public float SurfaceArea=16;public Vector3 AveragePosition;public List<ZoneSegment> Segments=new List<ZoneSegment>{new ZoneSegment(),new ZoneSegment(),new ZoneSegment(),new ZoneSegment()};}
public class ZoneWallObject:ZoneObject {public List<ZoneWallPlatformObject> Platforms=new List<ZoneWallPlatformObject>();}
public class ZoneWallPlatformObject {public GameObject gameObject=new GameObject();public ZoneWallObject WallZone;public int InstanceID,Height;public bool IsCeiling,DisabledByUser,DisabledButVisible;public ulong SurfaceGUID=1;public Color[] Colors=new[]{new Color(0,1,0)};}
public class ZoneManager {public static ZoneManager Instance=new ZoneManager();public bool ThrowRead;public List<ZoneObject> Zones=new List<ZoneObject>();public HashSet<ZoneWallObject> DirtyWallZones=new HashSet<ZoneWallObject>();public List<ZoneObject> GetLotZones(ulong lot){if(ThrowRead)throw new Exception("zone read failure");return Zones;}}
public static class BuildModeRefreshManager {public static bool IsZoneStateRefreshed=true;}
public class ParaSystemBase {}
public class SystemManager {public static SystemManager Instance=new SystemManager();public List<ParaSystemBase> SystemsToUpdate=new List<ParaSystemBase>();}
public class UpdatePaintMode:ParaSystemBase {
 public int Calls;public bool Succeed=true,Throw;public ZoneWallPlatformObject Seen;public bool Underside;
 private void PaintPlatform(Player p,int id,bool paintCeillings){Calls++;Seen=p.PlatformHovered;Underside=p.PlatformHoveredIsUnderside;if(paintCeillings)throw new Exception("ceiling call");if(Throw)throw new Exception("native failure");if(Succeed){Seen.SurfaceGUID=p.SelectedSurface;Seen.Colors=p.SelectedColors;}}
}
public class MoneyManager {public static MoneyManager Instance=new MoneyManager();public bool Affordable=true;public bool CanPurchase(int cost)=>Affordable;}
namespace ParalivesAccess.Speech {public static class ScreenReader {public static List<string> Spoken=new List<string>();public static void Speak(string s){Spoken.Add(s);}public static string NormalizeForOutput(string s)=>s;}}
namespace ParalivesAccess {
 public class Logger {public void LogError(string s){}public void LogWarning(string s){}public void LogInfo(string s){}}
 public static class Plugin {public static Logger Log=new Logger();}
 public static class Loc {public static string Get(string key,params object[] args)=>key+"("+string.Join(";",args)+")";}
}
namespace ParalivesAccess.Handlers { public static class LotCursorHandler {public static bool OwnsInput=>false;}
 public static class ModInputKey {public static bool Available=true;public static bool WasPressed(Keyboard k,Key key)=>k?.Pressed==key;public static bool IsAvailable(HybridPlayer h,Key key,out string conflict){conflict="hotkey_conflict";return Available;}}
 public static class MenuNavigationHandler {public static Focusable ActivePanel;public static int Released,Cancelled;public static void PressFocusable(Focusable f){}public static void ReleaseFocusable(Focusable f){Released++;(f as UIBuildModeItem)?.Select();}public static void CancelFocusablePress(Focusable f){Cancelled++;}}
 public static class ItemDescription {public static string GetProductIdentity(CatalogueItem i,ulong skin)=>"furniture "+i.GUID;}
 public static class RoomLabelHandler {public static string Name(ZoneWallObject room)=>"Test room";}
 public static class UiLabelReader {LABEL_METHOD public static string GetLabelOrFallback(Focusable f)=>f is UIBuildModeItem row?GetBuildModeItemLabel(row):"category";public static string GetLabel(Focusable f)=>GetLabelOrFallback(f);}
 public static class ItemPlacementHandler {public static bool OwnsInput;public static ulong Lot=1;public static float GridSpacingMetres=0.5f;public static ulong ResolvePlacementLot(Player p,out string source){source="camera";return Lot;}}
 public static class WallConstructionHandler {public static bool OwnsInput,Enabled=true;}
 public static class TutorialHandler {public static bool OwnsInput;}
 public static class ContextActionHandler {public static bool OwnsInput;}
 public static class OccupationHandler {public static bool OwnsInput;}
 public static class LotObjectHandler {public static bool OwnsInput;}
 public class PlacementGrid {}
 public static class PlacementGridReader {public static bool TryRead(ulong lot,float spacing,out PlacementGrid grid){grid=new PlacementGrid();return true;}public static string Position(PlacementGrid g,Vector3 p)=>"Accessibility grid";public static string Length(float v)=>v+" m";}
 public static class WorldTargeting {public static bool Succeed=true;public static int Calls;public static bool FrameArea(Player p,ulong lot,int layer,Vector3 point,string source){Calls++;return Succeed;}}
 public static class FloorChecks {
  static int checks;static HybridPlayer H=>PlayerManager.Instance.HybridPlayer1;
  static UpdatePaintMode Native;static ZoneWallPlatformObject Floor;
  static object Invoke(Type t,string method,params object[] args)=>t.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
  static void Check(bool ok,string name){if(!ok)throw new Exception(name+"; "+string.Join(" | ",ScreenReader.Spoken));checks++;}
  static bool Said(string key)=>ScreenReader.Spoken.Any(s=>s.Contains(key+"("));
  static void Reset(){
   Invoke(typeof(FloorCoveringHandler),"Close",false,true);Invoke(typeof(BuildCatalogHandler),"Close",null,false,true);
   PlayerManager.Instance=new PlayerManager();ZoneManager.Instance=new ZoneManager();SystemManager.Instance=new SystemManager();UI.Catalog=new UIBuildModeCatalog();
   Native=new UpdatePaintMode();SystemManager.Instance.SystemsToUpdate.Add(Native);Keyboard.current=new Keyboard();MoneyManager.Instance=new MoneyManager();
   Settings.Get<Surfaces>().Data=new Dictionary<ulong,Surface>{{1,new Surface{GUID=1,DisplayName="Old"}},{2,new Surface{GUID=2,DisplayName="Oak"}}};
   var room=new ZoneWallObject{InstanceID=10};Floor=new ZoneWallPlatformObject{InstanceID=11,WallZone=room};room.Platforms.Add(Floor);ZoneManager.Instance.Zones.Add(room);
   ItemPlacementHandler.Lot=1;FloorCoveringHandler.Enabled=BuildCatalogHandler.Enabled=true;BuildModeRefreshManager.IsZoneStateRefreshed=true;ModInputKey.Available=true;WorldTargeting.Succeed=true;WorldTargeting.Calls=0;
   ScreenReader.Spoken.Clear();Time.frameCount++;MenuNavigationHandler.Released=MenuNavigationHandler.Cancelled=0;
  }
  static void Next(){Time.frameCount++;Keyboard.current=new Keyboard();}
  static void Open(){FloorCoveringHandler.Open(H);Next();}
  static void Enter(){Keyboard.current.enterKey.wasReleasedThisFrame=true;FloorCoveringHandler.Tick();Next();}
  static void Review(){Open();Enter();Check(Native.Calls==0&&Said("floor_cover_review"),"first Enter reviews without applying");}
  static UIBuildModeItem SurfaceRow(){var row=new UIBuildModeItem{ItemData=new CatalogueItem{GUID=999},Price=9999};row.Bind(UIBuildModeItemTypes.Surfaces,Settings.Get<Surfaces>().GetSurfaceByGUID(2));return row;}
  static void OpenCatalog(UIBuildModeItem row){UI.Catalog.ItemList.UIList.CurrentItems.Add(row);Keyboard.current.Pressed=Key.F6;BuildCatalogHandler.Tick();Next();}
  static void CatalogEnter(){Keyboard.current.enterKey.wasPressedThisFrame=true;BuildCatalogHandler.Tick();Next();Keyboard.current.enterKey.wasReleasedThisFrame=true;BuildCatalogHandler.Tick();}
  public static string Run(){
   Reset();var upper=new ZoneWallPlatformObject{InstanceID=12,WallZone=Floor.WallZone,Height=2};Floor.WallZone.Platforms.Add(upper);Open();
   string lowerName=(string)Invoke(typeof(FloorCoveringHandler),"TargetName",Floor),upperName=(string)Invoke(typeof(FloorCoveringHandler),"TargetName",upper);
   Check(lowerName!=upperName&&lowerName.Contains("Test room")&&upperName.Contains("Test room"),"two live platforms in one room have distinct named targets");
   upper.DisabledByUser=true;Check((string)Invoke(typeof(FloorCoveringHandler),"TargetName",Floor)=="Test room","a single live platform uses the concise room name");
   Check(CatalogEntryReader.ReadableSurfaceName("FloorWoodHardOak").Contains("Oak")&&!CatalogEntryReader.ReadableSurfaceName("FloorWoodHardOak").Contains("WoodHard"),"missing oak label removes engine material prefix");
   Check(CatalogEntryReader.ReadableSurfaceName("FloorWoodSoftPine").Contains("Pine")&&CatalogEntryReader.ReadableSurfaceName("FloorTileCeramicHex").Contains("Ceramic Hex"),"fallback retains material and pattern distinctions");
   Reset();var row=SurfaceRow();Check(!CatalogEntryReader.IsOrdinaryItem(row),"pooled surface is not stale ItemData furniture");
   Check(CatalogEntryReader.SurfaceName(2)=="translated Surface_Oak","game localization takes precedence over fallback names");
   Check(UiLabelReader.GetLabelOrFallback(row).Contains("translated Surface_Oak")&&!UiLabelReader.GetLabelOrFallback(row).Contains("9999"),"normal UI label uses surface discriminant instead of stale furniture price");
   row.Bind(UIBuildModeItemTypes.Items);Check(CatalogEntryReader.SurfaceOf(row)==null&&CatalogEntryReader.IsOrdinaryItem(row),"pooled furniture is not stale surface");
   row.Bind(UIBuildModeItemTypes.Fences,Settings.Get<Surfaces>().GetSurfaceByGUID(2));Check(!CatalogEntryReader.IsOrdinaryItem(row)&&CatalogEntryReader.SurfaceOf(row)==null,"unsupported native row kinds not misclassified");
   Reset();H.Player.State=GameStates.BuildMode;H.TownMap=true;UI.Catalog.IsVisible=false;OpenCatalog(SurfaceRow());
   Check(Said("catalog_town_map")&&!BuildCatalogHandler.OwnsInput&&!H.Player.FreezeCameraMovement,"F6 at map zoom explains the F7 route without forcing a hidden catalogue open");
   H.TownMap=false;UI.Catalog.IsVisible=true;Keyboard.current.Pressed=Key.F6;BuildCatalogHandler.Tick();Next();
   Check(BuildCatalogHandler.OwnsInput,"F6 opens normally after area framing restores the native catalogue");
   Reset();H.Player.State=GameStates.BuildMode;H.TownMap=true;H.Player.TownMapForFastTravelMethod=1;UI.Catalog.IsVisible=false;OpenCatalog(SurfaceRow());
   Check(Said("catalog_unavailable")&&!Said("catalog_town_map"),"fast travel is not misreported as ordinary map zoom");
   Reset();H.Player.State=GameStates.LiveMode;H.TownMap=true;UI.Catalog.IsVisible=false;OpenCatalog(SurfaceRow());
   Check(Said("catalog_unavailable")&&!Said("catalog_town_map"),"live-mode map does not offer build recovery instructions");
   Reset();row=SurfaceRow();MoneyManager.Instance.Affordable=false;OpenCatalog(row);Check(BuildCatalogHandler.OwnsInput&&ScreenReader.Spoken.Any(s=>s.Contains("catalog_surface(")),"F6 exposes a surface without furniture affordability filter");
   CatalogEnter();Check(MenuNavigationHandler.Released==1&&FloorCoveringHandler.OwnsInput&&!BuildCatalogHandler.OwnsInput,"native surface selection hands ownership to floor targets");
   FloorCoveringHandler.Tick();Check(!Said("floor_cover_review")&&Native.Calls==0,"catalogue Enter cannot fall through into review or paint");
   Check(FloorCoveringHandler.TryGetContext(H,out string context)&&context.Contains("translated Surface_Oak")&&context.Contains("floor_cover_target"),"floor F3 names live target and selected material");
   Reset();row=SurfaceRow();OpenCatalog(row);Keyboard.current.enterKey.wasPressedThisFrame=true;BuildCatalogHandler.Tick();Next();row.Bind(UIBuildModeItemTypes.Items);Keyboard.current.enterKey.wasReleasedThisFrame=true;BuildCatalogHandler.Tick();Check(MenuNavigationHandler.Released==0&&MenuNavigationHandler.Cancelled==1,"surface row rebound to furniture between press and release is not activated");
   Reset();row=SurfaceRow();OpenCatalog(row);Keyboard.current.ctrlKey.isPressed=true;CatalogEnter();Check(MenuNavigationHandler.Released==0,"modifier prevents native thumbnail shortcut instead of surface selection");
   Reset();row=SurfaceRow();row.Selects=false;H.Player.SelectedSurface=1;OpenCatalog(row);CatalogEnter();Check(!FloorCoveringHandler.OwnsInput&&Said("catalog_surface_not_selected"),"failed native selection cannot open application controls");
   Reset();row=SurfaceRow();row.Category=PaintCategory.Walls;OpenCatalog(row);CatalogEnter();Check(!FloorCoveringHandler.OwnsInput&&Said("catalog_surface_selected")&&BuildCatalogHandler.OwnsInput,"wall surface selection retains native category without opening floor application");
   Reset();row=SurfaceRow();OpenCatalog(row);ZoneManager.Instance.ThrowRead=true;CatalogEnter();Check(!FloorCoveringHandler.OwnsInput&&!H.Player.FreezeCameraMovement&&BuildCatalogHandler.Enabled&&Said("floor_cover_failed"),"opening failure releases camera without disabling unrelated catalogue controls");
   Reset();Review();Enter();Check(Native.Calls==1&&Native.Seen==Floor&&!Native.Underside&&Floor.SurfaceGUID==2,"second Enter calls native floor operation for exact reviewed target");
   Check(Said("floor_cover_applied"),"success announced only after material and colors match");Enter();Check(Native.Calls==1&&Said("floor_cover_unchanged"),"reapplying identical covering does not create a redundant operation");
   Reset();Review();var oldHover=new ZoneWallPlatformObject();H.Player.PlatformHovered=oldHover;H.Player.PlatformHoveredIsUnderside=true;Enter();Check(H.Player.PlatformHovered==oldHover&&H.Player.PlatformHoveredIsUnderside,"native hover context restored after success");
   Reset();Review();oldHover=new ZoneWallPlatformObject();H.Player.PlatformHovered=oldHover;H.Player.PlatformHoveredIsUnderside=true;Native.Throw=true;Enter();Check(H.Player.PlatformHovered==oldHover&&H.Player.PlatformHoveredIsUnderside,"native hover restored when paint throws");Check(!FloorCoveringHandler.OwnsInput&&Said("floor_cover_failed")&&!Said("floor_cover_applied"),"exception stops without retry or success claim");
   Reset();Review();Native.Succeed=false;Enter();Check(Said("floor_cover_not_verified")&&!Said("floor_cover_applied"),"native no-op is not called applied");
   Reset();Review();SystemManager.Instance.SystemsToUpdate.Clear();Enter();Check(Native.Calls==0&&Said("floor_cover_unavailable"),"missing registered native operation refuses application");
   Reset();Review();H.Player.SelectedColors[0]=new Color(0,0,1);Enter();Check(Native.Calls==0&&Said("floor_cover_review_changed"),"in-place color-array mutation invalidates snapshot");
   Reset();Review();H.Player.SelectedSurface=1;Enter();Check(Native.Calls==0,"material change requires new review");
   Reset();Review();Settings.Get<Surfaces>().Data.Remove(2);Enter();Check(Native.Calls==0,"removed surface cannot be applied");
   Reset();Review();Floor.Colors[0]=new Color(0,0,1);Enter();Check(Native.Calls==0,"concurrent existing-covering edit invalidates review");
   Reset();Review();Floor.WallZone.SurfaceArea=20;Enter();Check(Native.Calls==0,"room resize invalidates reviewed area");
   Reset();Review();Floor.WallZone.Segments[0].Segment.SegmentPoints[1]=new Vector3(1,0,1);Enter();Check(Native.Calls==0,"same-area boundary change invalidates review");
   Reset();Review();Floor.Height=2;Enter();Check(Native.Calls==0,"platform height change requires review");
   Reset();Review();ZoneManager.Instance.Zones.Clear();Enter();Check(Native.Calls==0&&Said("floor_cover_targets_changed"),"removed room cannot retain a stale paint target");
   Reset();Review();BuildModeRefreshManager.IsZoneStateRefreshed=false;Enter();Check(Native.Calls==0,"unsettled native zones cannot be painted from review");
   Reset();Review();ZoneManager.Instance.DirtyWallZones.Add(Floor.WallZone);Enter();Check(Native.Calls==0,"dirty room geometry requires renewed review");
   Reset();Floor.IsCeiling=true;Open();Enter();Check(Native.Calls==0&&Said("floor_cover_no_targets"),"ceiling platform excluded from floor workflow");
   Reset();Floor.DisabledByUser=true;Open();Check(Said("floor_cover_no_targets"),"user-disabled floor excluded");
   Reset();Floor.DisabledButVisible=true;Open();Check(Said("floor_cover_no_targets"),"disabled preview floor excluded");
   Reset();Floor.gameObject.activeInHierarchy=false;Open();Check(Said("floor_cover_no_targets"),"inactive platform excluded");
   Reset();Floor.WallZone.Layer=1;Open();Check(Said("floor_cover_no_targets"),"another floor layer excluded");
   Reset();Floor.WallZone.LotPlacedOnGUID=9;Open();Check(Said("floor_cover_no_targets"),"another lot excluded");
   Reset();Review();H.Player.Layer=1;Enter();Check(Native.Calls==0&&!FloorCoveringHandler.OwnsInput&&Said("floor_cover_context_changed"),"camera floor change closes old review");
   Reset();Review();ItemPlacementHandler.Lot=9;Enter();Check(Native.Calls==0&&!FloorCoveringHandler.OwnsInput,"camera lot change closes old review");
   Reset();Review();H.HasInputFieldFocused=true;FloorCoveringHandler.BeforeGameSystems();Check(!FloorCoveringHandler.OwnsInput&&!H.Player.FreezeCameraMovement,"text entry releases input and camera");
   Reset();Review();H.Player.PlayerActionBlockedByUIWindow=true;FloorCoveringHandler.BeforeGameSystems();Check(!FloorCoveringHandler.OwnsInput,"modal has priority");
   Reset();Review();H.IsUsingGamePad=true;FloorCoveringHandler.BeforeGameSystems();Check(!FloorCoveringHandler.OwnsInput,"gamepad has priority");
   Reset();Review();H.ButtonUndo.Down=true;FloorCoveringHandler.BeforeGameSystems();Check(!FloorCoveringHandler.OwnsInput&&H.ButtonUndo.Down&&Native.Calls==0,"native undo exits review without painting and retains its input");
   Reset();Review();H.ButtonCancel.Down=true;FloorCoveringHandler.BeforeGameSystems();Check(!FloorCoveringHandler.OwnsInput&&H.ButtonCancel.Down,"native Escape remains available");
   Reset();Review();H.ButtonConfirm.Down=true;FloorCoveringHandler.BeforeGameSystems();Check(!FloorCoveringHandler.OwnsInput&&H.ButtonConfirm.Down&&Native.Calls==0,"physical mouse click relinquishes semantic target");
   Reset();Review();Keyboard.current.Pressed=Key.F6;FloorCoveringHandler.BeforeGameSystems();Check(!LotBuildInputOwnership.OwnsInput,"F6 yields shared ownership without latch blocking catalogue");
   Reset();Review();Keyboard.current.rightArrowKey.wasPressedThisFrame=true;FloorCoveringHandler.Tick();Next();Enter();Check(Native.Calls==0,"moving selection cancels review even with one target");
   Reset();Open();ModInputKey.Available=false;Keyboard.current.homeKey.wasPressedThisFrame=true;FloorCoveringHandler.Tick();Check(WorldTargeting.Calls==0&&ScreenReader.Spoken.Contains("hotkey_conflict"),"rebound Home never moves the camera");
   Reset();Open();WorldTargeting.Succeed=false;Keyboard.current.homeKey.wasPressedThisFrame=true;FloorCoveringHandler.Tick();Check(WorldTargeting.Calls==1&&Said("explorer_frame_failed"),"framing failure is audible");
   Reset();Open();Keyboard.current.homeKey.wasPressedThisFrame=true;FloorCoveringHandler.Tick();Check(WorldTargeting.Calls==1&&Said("floor_cover_framed"),"framing success is audible without applying");
   Reset();H.Player.FreezeCameraMovement=true;Open();H.ButtonCancel.Down=true;FloorCoveringHandler.BeforeGameSystems();Check(H.Player.FreezeCameraMovement,"closing restores the pre-existing frozen-camera baseline");
   return checks+" floor covering and catalogue checks passed; actual native rendering, undo, saves and speech remain runtime checks.";
  }
 }
}
'@
$adapters = $adapters.Replace('LABEL_METHOD', $labelMethod)
Add-Type -TypeDefinition ($adapters + "`n" + ($sources -join "`n")) -Language CSharp
[ParalivesAccess.Handlers.FloorChecks]::Run()
