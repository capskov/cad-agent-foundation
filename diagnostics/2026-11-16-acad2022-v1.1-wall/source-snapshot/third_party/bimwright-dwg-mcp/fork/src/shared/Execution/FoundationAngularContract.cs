using System;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution
{
 public static partial class FoundationDrawingPlan
 {
  static bool ValidateAngular(JObject s,ref int count)
  {
   if(s.Value<string>("op")!="angular_dimension")return false;
   Keys(s,"id","op","center","ray1","ray2","position","dimension_style","layer","color");foreach(string key in new[]{"center","ray1","ray2","position"})Point(s[key]);Text(s,"dimension_style",255);Options(s);
   double a=RayAngle(s["center"],s["ray1"]),sweep=NormalizeAngle(RayAngle(s["center"],s["ray2"])-a),place=NormalizeAngle(RayAngle(s["center"],s["position"])-a);
   if(sweep<=1e-9||sweep>=Math.PI-1e-9||place<=1e-9||place>=sweep-1e-9)throw new ArgumentException("CCW angle <180 degrees and arc position inside that wedge required");count++;return true;
  }
  static double RayAngle(JToken center,JToken point)
  {double x=Number(point[0])-Number(center[0]),y=Number(point[1])-Number(center[1]);if(Math.Sqrt(x*x+y*y)<=1e-9)throw new ArgumentException("angular ray must differ from center");return Math.Atan2(y,x);}
  static bool VerifyAngular(JObject s,JObject receipt)
  {
   string h=receipt["objects"]?[s.Value<string>("id")]?.Value<string>("entity");var e=h==null?null:receipt["after"]?[h] as JObject;
   if(e==null||e.Value<string>("type")!="LineAngularDimension2"||e.Value<string>("dimension_kind")!="angular_dimension"||e.Value<string>("dimension_style")!=s.Value<string>("dimension_style")||!EntityOptions(e,s)||e.Value<string>("dimension_text")!="")return false;
   if(!PointMatches(e["line1_start"],(JArray)s["center"])||!PointMatches(e["line1_end"],(JArray)s["ray1"])||!PointMatches(e["line2_start"],(JArray)s["center"])||!PointMatches(e["line2_end"],(JArray)s["ray2"]))return false;
   double expected=NormalizeAngle(RayAngle(s["center"],s["ray2"])-RayAngle(s["center"],s["ray1"]));if(!Near(e["measurement"],expected,1e-9))return false;
   // Native arc representative may move along the arc, but its radius must remain requested.
   if(e["arc_point"] is not JObject p)return false;double dx=Number(p["x"])-Number(s["center"][0]),dy=Number(p["y"])-Number(s["center"][1]);if(Math.Abs(Math.Sqrt(dx*dx+dy*dy)-Distance(s["position"],s["center"]))>.001)return false;
   var style=receipt["symbols_after"]?["dimension_styles"]?[s.Value<string>("dimension_style")];if(style==null)return false;foreach(string f in new[]{"text_height","arrow_size","overall_scale","gap","decimal_places","dimension_color","extension_color","text_color"})if(!Equivalent(e[f],style[f],f))return false;return true;
  }
 }
}
