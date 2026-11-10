$ErrorActionPreference = 'Stop'
$env:DOTNET_PROCESSOR_COUNT = '1'
$root = Split-Path -Parent $PSScriptRoot
$source = Get-Content -Raw (Join-Path $root 'src\Handlers\RoofGeometry.cs')
$checks = @'
public static class RoofChecks {
 static int count;
 static void Check(bool ok,string name){count++;if(!ok)throw new System.Exception(name);}
 static void Near(double a,double b,string name){Check(System.Math.Abs(a-b)<.000001,name);}
 public static void Run(){
  var old=System.Globalization.CultureInfo.CurrentCulture;
  try {
   foreach(var culture in new[]{"en-US","nl-NL"}){
    System.Globalization.CultureInfo.CurrentCulture=System.Globalization.CultureInfo.GetCultureInfo(culture);
    ParalivesAccess.Handlers.RoofGeometry.Dimensions s;
    Check(ParalivesAccess.Handlers.RoofGeometry.TryParse("6; 4; 2",out s),culture+" triple");Near(s.Width,6,"width");Near(s.Depth,4,"depth");Near(s.Height,2,"height");
    Check(ParalivesAccess.Handlers.RoofGeometry.TryParse(culture=="nl-NL"?"6,5; 4,25; 2,6":"6.5; 4.25; 2.6",out s),culture+" decimals");Near(s.Width,6.5,"decimal width");Near(s.Depth,4.25,"decimal depth");Near(s.Height,2.6,"decimal height");
    foreach(var bad in new[]{null,"","6;4","6;4;2;1","0;4;2","-1;4;2","6;NaN;2","6;4;Infinity","1e3;4;2","1001;4;2","1,000;4;2;3","6 / 4 / 2","0.000;4;2"})
     Check(!ParalivesAccess.Handlers.RoofGeometry.TryParse(bad,out s),"reject "+bad);
   }
   Check(ParalivesAccess.Handlers.RoofGeometry.ValidAxis(6,2,3,.4,10,true),"physical scale");
   Check(ParalivesAccess.Handlers.RoofGeometry.ValidAxis(6,2,3,.4,10,false),"fixed unchanged");
   Check(!ParalivesAccess.Handlers.RoofGeometry.ValidAxis(8,2,3,.4,10,false),"fixed changed");
   Check(!ParalivesAccess.Handlers.RoofGeometry.ValidAxis(.3,1,3,.4,10,true),"native minimum");
   Check(!ParalivesAccess.Handlers.RoofGeometry.ValidAxis(11,1,3,.4,10,true),"native maximum");
   Check(ParalivesAccess.Handlers.RoofGeometry.ValidAxis(10,1,3,.4,10,true),"inclusive maximum");
   foreach(var scale in new[]{0d,-1d,double.NaN,double.PositiveInfinity})Check(!ParalivesAccess.Handlers.RoofGeometry.ValidAxis(4,scale,3,.4,10,true),"invalid scale");
   foreach(var value in new[]{0d,-1d,double.NaN,double.PositiveInfinity,1001d})Check(!ParalivesAccess.Handlers.RoofGeometry.ValidAxis(value,1,3,.4,10,true),"invalid metres");
   var p=new[]{new ParalivesAccess.Handlers.RoofGeometry.Point(10,20),new ParalivesAccess.Handlers.RoofGeometry.Point(16,20),new ParalivesAccess.Handlers.RoofGeometry.Point(16,24),new ParalivesAccess.Handlers.RoofGeometry.Point(10,24)};
   ParalivesAccess.Handlers.RoofGeometry.Plan plan;
   Check(ParalivesAccess.Handlers.RoofGeometry.TryPlan(p,0,.25,out plan),"rectangle");Near(plan.X,13,"centre x");Near(plan.Z,22,"centre z");Near(plan.Width,6.5,"overhang width");Near(plan.Depth,4.5,"overhang depth");
   Check(ParalivesAccess.Handlers.RoofGeometry.TryPlan(p,90,.25,out plan),"quarter turn");Near(plan.X,13,"rotated centre x");Near(plan.Z,22,"rotated centre z");Near(plan.Width,4.5,"rotated width");Near(plan.Depth,6.5,"rotated depth");
   foreach(double angle in new[]{-135d,-45d,30d,45d,135d,360d}){
    Check(ParalivesAccess.Handlers.RoofGeometry.TryPlan(p,angle,.25,out plan),"angled envelope");Near(plan.X,13,"angled centre x");Near(plan.Z,22,"angled centre z");
    double c=System.Math.Cos(angle*System.Math.PI/180),s=System.Math.Sin(angle*System.Math.PI/180);
    foreach(var point in p){double x=point.X-plan.X,z=point.Z-plan.Z;Check(System.Math.Abs(c*x-s*z)<=plan.Width/2-.249999&&System.Math.Abs(s*x+c*z)<=plan.Depth/2-.249999,"encloses boundary with overhang");}
   }
   Check(!ParalivesAccess.Handlers.RoofGeometry.TryPlan(null,0,.25,out plan),"missing boundary");
   Check(!ParalivesAccess.Handlers.RoofGeometry.TryPlan(new[]{p[0],p[1]},0,.25,out plan),"short boundary");
   Check(!ParalivesAccess.Handlers.RoofGeometry.TryPlan(new[]{p[0],p[0],p[0]},0,.25,out plan),"degenerate");
   Check(!ParalivesAccess.Handlers.RoofGeometry.TryPlan(p,double.NaN,.25,out plan),"bad angle");
   foreach(var padding in new[]{-.1,double.NaN,double.PositiveInfinity,11d})Check(!ParalivesAccess.Handlers.RoofGeometry.TryPlan(p,0,padding,out plan),"bad overhang");
   var reversed=(ParalivesAccess.Handlers.RoofGeometry.Point[])p.Clone();System.Array.Reverse(reversed);
   Check(ParalivesAccess.Handlers.RoofGeometry.TryPlan(reversed,0,.25,out plan),"reversed contour");Near(plan.Width,6.5,"reversed width");
  }finally{System.Globalization.CultureInfo.CurrentCulture=old;}
  System.Console.WriteLine(count+" roof geometry and dimension checks passed; native ceiling support, resize transactions, undo and speech require real-player checks.");
 }
}
'@
Add-Type -TypeDefinition ($source + $checks) -Language CSharp
[RoofChecks]::Run()
