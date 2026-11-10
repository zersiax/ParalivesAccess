$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -Encoding UTF8 (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Handlers/AccessConnectivity.cs')
$checks = @'
namespace ParalivesAccess.Handlers {
 public static class ConnectivityChecks {
  public static string Run(){int checks=0;
   var line=new[]{new[]{1},new[]{0,2},new[]{1,3},new[]{2,4},new[]{3}};var one=new[]{0,0,0,0,0};
   Action<bool,string> check=(ok,name)=>{if(!ok)throw new Exception(name);checks++;};
   check(AccessConnectivity.Preserved(line,one,new[]{false,false,false,false,false},(a,b)=>true),"unchanged corridor remains connected");
   check(!AccessConnectivity.Preserved(line,one,new[]{false,false,true,false,false},(a,b)=>true),"furniture closing the only corridor is rejected");
   check(AccessConnectivity.Preserved(line,one,new[]{true,false,false,false,false},(a,b)=>true),"occupying an end alcove leaves the remaining route intact");
   check(!AccessConnectivity.Preserved(line,one,new[]{false,false,false,false,false},(a,b)=>!(a==2&&b==3)&&!(a==3&&b==2)),"footprint cutting only an edge still breaks access");
   var ring=new[]{new[]{1,3},new[]{0,2},new[]{1,3},new[]{0,2}};
   check(AccessConnectivity.Preserved(ring,new[]{0,0,0,0},new[]{false,true,false,false},(a,b)=>true),"an alternate route around furniture remains usable");
   check(AccessConnectivity.Preserved(ring,new[]{0,0,0,0},new[]{false,false,false,false},(a,b)=>!(a==0&&b==1)&&!(a==1&&b==0)),"a blocked direct edge can use the longer route");
   check(!AccessConnectivity.Preserved(ring,new[]{0,0,0,0},new[]{false,true,false,true},(a,b)=>true),"two barriers isolate the remaining pockets");
   var separate=new[]{new[]{1},new[]{0},new[]{3},new[]{2}};
   check(AccessConnectivity.Preserved(separate,new[]{0,0,1,1},new[]{false,false,false,false},(a,b)=>true),"existing disconnected areas are not a newly introduced fault");
   check(AccessConnectivity.Preserved(separate,new[]{0,0,1,1},new[]{true,false,false,false},(a,b)=>true),"each original component is checked independently");
   check(AccessConnectivity.Preserved(line,one,new[]{true,true,true,true,true},(a,b)=>true),"connectivity alone defers fully occupied floor to approach/footprint guards");
   check(AccessConnectivity.Preserved(Array.Empty<int[]>(),Array.Empty<int>(),Array.Empty<bool>(),(a,b)=>true),"empty graph is rejected by the separate native graph availability gate");
   return checks+" access-connectivity cases passed; native navmesh, footprints and interaction use require player checks.";
  }
 }
}
'@
Add-Type -TypeDefinition ($source + "`n" + $checks) -Language CSharp
[ParalivesAccess.Handlers.ConnectivityChecks]::Run()
