// Explicit standalone line/circle edit contracts; positive offset is left / outward.
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution
{
 public static partial class FoundationDrawingPlan
 {
  public static string[] CurveOperations=>new[]{"offset","mirror_copy"};
  static bool ValidateCurve(JObject s,ref int count)
  {
   string op=s.Value<string>("op");if(!CurveOperations.Contains(op))return false;
   if(op=="offset"){Keys(s,"id","op","targets","distance");if(Math.Abs(Number(s["distance"]))<=1e-9)throw new ArgumentException("nonzero signed offset required");}
   else{Keys(s,"id","op","targets","axis_start","axis_end");Point(s["axis_start"]);Point(s["axis_end"]);if(Distance(s["axis_start"],s["axis_end"])<=1e-9)throw new ArgumentException("distinct mirror axis endpoints required");}
   ValidateTargets(s,ref count);if(((JArray)s["targets"]).Count!=1)throw new ArgumentException("one standalone Line or Circle required");count++;return true;
  }
  public static JObject CurveExpected(JObject source,JObject s)
  {
   var e=(JObject)source.DeepClone();string type=e.Value<string>("type"),op=s.Value<string>("op");if(type is not ("Line" or "Circle"))throw new ArgumentException("standalone Line/Circle only");
   void Set(string key,double x,double y){var p=(JObject)e[key];p["x"]=x;p["y"]=y;}
   if(op=="offset")
   {
    double d=Number(s["distance"]);
    if(type=="Circle")
    {double radius=Number(e["radius"])+d;if(radius<=0)throw new ArgumentException("offset circle radius must remain positive");e["radius"]=radius;e["diameter"]=2*radius;e["circumference"]=2*Math.PI*radius;e["area"]=Math.PI*radius*radius;}
    else
    {double dx=Number(e["end"]["x"])-Number(e["start"]["x"]),dy=Number(e["end"]["y"])-Number(e["start"]["y"]),length=Math.Sqrt(dx*dx+dy*dy);if(length<=1e-9)throw new ArgumentException("nonzero line required");foreach(string key in new[]{"start","end"})Set(key,Number(e[key]["x"])-dy*d/length,Number(e[key]["y"])+dx*d/length);}
   }
   else
   {
    double ax=Number(s["axis_start"][0]),ay=Number(s["axis_start"][1]),dx=Number(s["axis_end"][0])-ax,dy=Number(s["axis_end"][1])-ay,len=dx*dx+dy*dy;
    foreach(string key in type=="Circle"?new[]{"center"}:new[]{"start","end"}){double x=Number(e[key]["x"])-ax,y=Number(e[key]["y"])-ay,t=(x*dx+y*dy)/len;Set(key,ax+2*t*dx-x,ay+2*t*dy-y);}
   }
   return e;
  }
  static bool VerifyCurve(JObject s,JObject receipt,out bool handled)
  {
   handled=CurveOperations.Contains(s.Value<string>("op"));if(!handled)return true;
   var t=(JObject)s["targets"][0];string h=t.Value<string>("handle");var before=receipt["before"]?[h] as JObject;var after=receipt["after"] as JObject;var map=receipt["objects"]?[s.Value<string>("id")] as JObject;
   if(before==null||after==null||map==null||Hash(before)!=t.Value<string>("expected_sha256")||!Equivalent(before,after[h]))return false;
   string nh=map.Value<string>("entity");if(nh==null||nh==h||map.Value<string>("source")!=h||after[nh] is not JObject actual)return false;
   var expected=CurveExpected(before,s);expected["handle"]=nh;expected.Remove("extents");var a=(JObject)actual.DeepClone();a.Remove("extents");return Equivalent(expected,a);
  }
 }
}
