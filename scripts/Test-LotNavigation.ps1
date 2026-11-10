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
$shortcuts = Get-Content -Raw (Join-Path $projectRoot 'src/Handlers/LotNavigationShortcuts.cs')
$methods = @('internal static void ConfigureParaListKey(', 'private static bool HandleQuickNavigation(') | ForEach-Object { Read-Method $shortcuts $_ }
$nearby = Read-Method (Get-Content -Raw (Join-Path $projectRoot 'src/Handlers/LotNearbyReader.cs')) 'internal static string Offset('
$grid = (Get-Content -Raw (Join-Path $projectRoot 'src/Handlers/PlacementGrid.cs')) -replace '(?m)^using [^;]+;\r?\n', ''
$source = @'
using System;using System.Linq;using UnityEngine;using UnityEngine.InputSystem;
namespace UnityEngine {public struct Vector3 {public float x,y,z;public Vector3(float a,float b,float c){x=a;y=b;z=c;}}}
namespace UnityEngine.InputSystem {
 public enum Key {None,N,F1=40,F2,F3,F4,F5,F6,F7,F8,F9,F10,F11,F12}
 public class Button {public bool isPressed;}
 public class Keyboard {public Key Pressed;public Button ctrlKey=new Button(),shiftKey=new Button(),altKey=new Button(),leftMetaKey=new Button(),rightMetaKey=new Button();}
}
public class HybridPlayer {}
namespace ParalivesAccess.Handlers {
 public static class Plugin {public static Logger Log=new Logger();}public class Logger {public void LogError(string s){}}
 public static class TimeDateHandler {public static Key ReadKey=Key.F2;}
 public static class BuildCatalogHandler {public static Key OpenKey=Key.F6;}
 public static class ContextActionHandler {public static Key ActionKey=Key.F4;}
 public static class VirtualAudioListenerHandler {public static Key ToggleKey=Key.F8;}
 public static class ParaSwitchHandler {public static Key SwitchKey=Key.F9;}
 public static class ModInputKey {public static bool WasPressed(Keyboard keys,Key key)=>key!=Key.None&&keys.Pressed==key;}
 public static class Loc {public static string Get(string key,params object[] args)=>key+"("+string.Join(";",args)+")";}
 public static class PlacementGridReader {public static string Length(float n)=>n.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture);}
 public static class LotObjectHandler {
  static Key OpenKey=Key.F7;static Key ParaListKey=Key.F7;static bool _directParas;static object _panel;static int Rooms,Paras,Closed;
  static void Open(HybridPlayer h,bool rooms=false,bool paras=false){if(rooms)Rooms++;if(paras)Paras++;_panel=new object();_directParas=paras;}
  static void Close(bool announce){Closed++;_panel=null;_directParas=false;}
  METHODS
  public static string Run(){int checks=0;Action<bool,string> check=(ok,name)=>{if(!ok)throw new Exception(name);checks++;};
   ConfigureParaListKey(Key.F7);check(ParaListKey==Key.F7,"default Ctrl+F7 is allowed beside ordinary F7");
   foreach(var key in new[]{Key.F1,Key.F2,Key.F3,Key.F4,Key.F5,Key.F6,Key.F8,Key.F9,Key.F10,Key.F11,Key.F12,Key.N}){ConfigureParaListKey(key);check(ParaListKey==Key.None,"reserved key refused: "+key);}
   ConfigureParaListKey(Key.None);check(ParaListKey==Key.None,"None disables direct Para shortcut");ConfigureParaListKey(Key.F7);
   var keys=new Keyboard{Pressed=Key.F7};var h=new HybridPlayer();check(!HandleQuickNavigation(h,keys)&&Rooms+Paras==0,"ordinary F7 retains explorer path");
   keys.shiftKey.isPressed=true;check(HandleQuickNavigation(h,keys)&&Rooms==1&&Paras==0,"Shift+F7 dispatches flat room list");
   keys.shiftKey.isPressed=false;keys.ctrlKey.isPressed=true;check(HandleQuickNavigation(h,keys)&&Paras==1,"Ctrl+F7 dispatches direct Paras");
   check(HandleQuickNavigation(h,keys)&&Closed==1&&_panel==null,"repeat chord closes direct Para view");
   keys.altKey.isPressed=true;check(!HandleQuickNavigation(h,keys)&&Paras==1,"Alt chord does not dispatch Paras");keys.altKey.isPressed=false;keys.leftMetaKey.isPressed=true;check(!HandleQuickNavigation(h,keys),"Meta chord does not dispatch");
   return checks+" shortcut configuration/modifier checks passed";
  }
 }
 public static class NearbyChecks {
  NEARBY
  public static string Run(){int checks=0;Action<bool,string> check=(ok,name)=>{if(!ok)throw new Exception(name);checks++;};
   foreach(double angle in new[]{0d,37d,90d,180d,-45d}){var grid=new PlacementGrid(123,-456,angle,.5);grid.World(2,3,out double x,out double z);grid.World(4,1,out double tx,out double tz);var a=new Vector3((float)x,100,(float)z);var b=new Vector3((float)tx,-100,(float)tz);string text=Offset(grid,a,b);check(text.Contains("target_right(1)")&&text.Contains("target_front(1)"),"rotated grid directions and metre distances: "+angle);text=Offset(grid,b,a);check(text.Contains("target_left(1)")&&text.Contains("target_back(1)"),"reverse relative direction: "+angle);check(Offset(grid,a,a).Contains("target_same_position"),"same point: "+angle);}
   return checks+" nearby fixed-grid offset checks passed; Unity objects, navigation and speech remain native checks";
  }
 }
}
'@
$source = $source.Replace('METHODS', ($methods -join "`n")).Replace('NEARBY', $nearby)
Add-Type -TypeDefinition ($source + "`n" + $grid) -Language CSharp
[ParalivesAccess.Handlers.LotObjectHandler]::Run()
[ParalivesAccess.Handlers.NearbyChecks]::Run()
