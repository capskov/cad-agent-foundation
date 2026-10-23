// Foundation native-operation extension, capskov 2026. Apache-2.0.
using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Shared.Execution
{
 public static partial class FoundationDrawingPlan
 {
  public static readonly string[] Operations=new[]{"layer","line","circle","polyline","text","mtext","callout","hatch","set_text","set_polyline","set_callout","set_callout_format","move","rotate","scale","copy","archive","save","text_style","set_text_style_font","dimension_style","set_layer","linear_dimension","aligned_dimension","radial_dimension","diameter_dimension"}.Concat(Phase2Operations).Concat(PrintOperations).Concat(CurveOperations).Concat(BlockOperations).Concat(new[]{"export_dxf","angular_dimension"}).ToArray();
  public static void ValidateAttachment(JObject s)
  {if(s["attachment"]!=null&&!new[]{"TopLeft","TopCenter","TopRight","MiddleLeft","MiddleCenter","MiddleRight","BottomLeft","BottomCenter","BottomRight"}.Contains(Text(s,"attachment",32)))throw new ArgumentException("known MTEXT attachment required");}
  public static void KeysWithOptional(JObject s,string[] required,params string[] optional)
  {
   if(required.Any(k=>s[k]==null)||s.Properties().Any(p=>!required.Contains(p.Name)&&!optional.Contains(p.Name)))throw new ArgumentException("unexpected or missing plan fields");
  }
  public static bool ValidateExtension(JObject s,ref int count)
  {
   var op=s.Value<string>("op");
   switch(op)
   {
    case "set_text_style_font":
     Keys(s,"id","op","name","expected_sha256","font_file","big_font_file");Text(s,"name",255);Digest(s,"expected_sha256");
     foreach(string key in new[]{"font_file","big_font_file"})
     {if(s[key]?.Type!=JTokenType.String)throw new ArgumentException("font basename required");string f=s.Value<string>(key);if(key=="big_font_file"&&f=="")continue;if(!System.Text.RegularExpressions.Regex.IsMatch(f,@"\A[^/\\:\x00-\x1f]+\.shx\z",System.Text.RegularExpressions.RegexOptions.IgnoreCase)||f.Length>255)throw new ArgumentException("replacement requires installed SHX basenames; empty big font allowed");}
     break;
    case "text_style":
     Keys(s,"id","op","name","font_file","big_font_file","width_factor");Text(s,"name",255);Text(s,"font_file",255);Positive(s,"width_factor");
     if(s["big_font_file"]?.Type!=JTokenType.String)throw new ArgumentException("big_font_file must be a string (empty for none)");
     foreach(string f in new[]{s.Value<string>("font_file"),s.Value<string>("big_font_file")})if(f.Contains('/')||f.Contains('\\')||f.Contains(':')||f.Contains('\0')||f.Length>255)throw new ArgumentException("font_file must be an installed font basename");
     break;
    case "dimension_style":
     KeysWithOptional(s,new[]{"id","op","name","text_style","text_height","arrow_size","overall_scale","gap","decimal_places","dimension_color","extension_color","text_color"},"extension_origin_offset","extension_beyond","decimal_separator");if(s["extension_origin_offset"]!=null&&Number(s["extension_origin_offset"])<0)throw new ArgumentException("nonnegative extension origin offset required");if(s["extension_beyond"]!=null&&Number(s["extension_beyond"])<0)throw new ArgumentException("nonnegative extension beyond required");if(s["decimal_separator"]!=null&&(s["decimal_separator"].Type!=JTokenType.String||s.Value<string>("decimal_separator").Length!=1||!"., ".Contains(s.Value<string>("decimal_separator"))))throw new ArgumentException("decimal separator must be dot comma or space");
     Text(s,"name",255);Text(s,"text_style",255);Positive(s,"text_height");Positive(s,"arrow_size");Positive(s,"overall_scale");Number(s["gap"]);
     foreach(string f in new[]{"dimension_color","extension_color","text_color"})Color(s,f);
     if(s["decimal_places"]?.Type!=JTokenType.Integer||s.Value<int>("decimal_places")<0||s.Value<int>("decimal_places")>8)throw new ArgumentException("decimal_places 0..8 required");break;
    case "set_layer":
     Keys(s,"id","op","name","expected_sha256","color","is_off","is_frozen","is_locked","is_plottable");Text(s,"name",255);Digest(s,"expected_sha256");Color(s,"color");foreach(string f in new[]{"is_off","is_frozen","is_locked","is_plottable"})Bool(s,f);break;
    case "set_callout_format":
     KeysWithOptional(s,new[]{"id","op","handle","expected_sha256","text_handle","text_expected_sha256","text_style","dimension_style","height","width","gap","text_color","leader_color"},"attachment");Expected(s);Expected(new JObject{["handle"]=s["text_handle"],["expected_sha256"]=s["text_expected_sha256"]});Text(s,"text_style",255);Text(s,"dimension_style",255);Positive(s,"height");Positive(s,"width");Number(s["gap"]);Color(s,"text_color");Color(s,"leader_color");ValidateAttachment(s);count+=2;break;
    case "linear_dimension":case "aligned_dimension":
     KeysWithOptional(s,new[]{"id","op","start","end","position","dimension_style","layer","color"},op=="linear_dimension"?new[]{"rotation"}:Array.Empty<string>());
     Point(s["start"]);Point(s["end"]);Point(s["position"]);Text(s,"dimension_style",255);Options(s);if(s["rotation"]!=null)Number(s["rotation"]);
     if(Distance(s["start"],s["end"])<=1e-9)throw new ArgumentException("dimension endpoints must differ");count++;break;
    case "radial_dimension":case "diameter_dimension":
     Keys(s,"id","op","center","chord","text_offset","dimension_style","layer","color");Point(s["center"]);Point(s["chord"]);Positive(s,"text_offset");Text(s,"dimension_style",255);Options(s);
     if(Distance(s["center"],s["chord"])<=1e-9)throw new ArgumentException("radius must be positive");count++;break;
    case "rotate":case "scale":case "copy":
     if(op=="copy")Keys(s,"id","op","targets","delta");else Keys(s,"id","op","targets","base",op=="rotate"?"angle":"factor");
     if(s["targets"] is not JArray ts||ts.Count<1||ts.Count>32)throw new ArgumentException("1..32 targets required");
     var ids=new HashSet<string>();foreach(JObject t in ts){Keys(t,"handle","expected_sha256");Expected(t);if(!ids.Add(t.Value<string>("handle")))throw new ArgumentException("duplicate target");}
     if(op=="copy")Point(s["delta"]);else{Point(s["base"]);if(op=="scale"){Positive(s,"factor");if(Number(s["factor"])<1e-6||Number(s["factor"])>1e6)throw new ArgumentException("scale factor outside 1e-6..1e6");}else Number(s["angle"]);}
     count+=ts.Count*(op=="copy"?2:1);break;
    default:return ValidateBlock(s,ref count)||ValidateAngular(s,ref count)||ValidateDxf(s,ref count)||ValidatePhase2(s,ref count)||ValidateCurve(s,ref count)||ValidatePrint(s);
   }
   return true;
  }
  public static void Digest(JObject s,string key){if(!System.Text.RegularExpressions.Regex.IsMatch(Text(s,key,64),"^[A-F0-9]{64}$"))throw new ArgumentException("native SHA256 required");}
  static double Distance(JToken a,JToken b)=>Math.Sqrt(Math.Pow(Number(a[0])-Number(b[0]),2)+Math.Pow(Number(a[1])-Number(b[1]),2));
  public static bool VerifyLayerChanges(JObject before,JObject after,JArray steps)
  {
   var changes=steps.Cast<JObject>().Where(s=>s.Value<string>("op")=="set_layer").ToArray();
   if(changes.Select(s=>s.Value<string>("name")).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=changes.Length)return false;
   foreach(var row in before.Properties())
   {
    var s=changes.SingleOrDefault(x=>x.Value<string>("name")==row.Name);var actual=after[row.Name] as JObject;
    if(s==null){if(!JToken.DeepEquals(row.Value,actual))return false;continue;}
    if(actual==null||Hash(row.Value)!=s.Value<string>("expected_sha256")||actual.Value<int?>("color_index")!=s.Value<int>("color"))return false;
    var old=(JObject)row.Value.DeepClone();var next=(JObject)actual.DeepClone();
    foreach(var f in new[]{"is_off","is_frozen","is_locked","is_plottable"}){if(actual.Value<bool?>(f)!=s.Value<bool>(f))return false;old.Remove(f);next.Remove(f);}
    old.Remove("color_index");next.Remove("color_index");if(!JToken.DeepEquals(old,next))return false;
   }
   return changes.All(s=>before[s.Value<string>("name")]!=null);
  }
  public static bool VerifySymbols(JObject receipt,JArray steps)
  {
   bool needs=steps.Cast<JObject>().Any(s=>s.Value<string>("op") is "text_style" or "set_text_style_font" or "dimension_style"||s["text_style"]!=null||s["dimension_style"]!=null);
   if(receipt["symbols_before"]==null||receipt["symbols_before"].Type==JTokenType.Null||receipt["symbols_after"]==null||receipt["symbols_after"].Type==JTokenType.Null)return !needs;
   if(receipt["symbols_before"] is not JObject old||receipt["symbols_after"] is not JObject next)return false;
   foreach(string table in new[]{"text_styles","dimension_styles"})
   {
    if(old[table] is not JObject a||next[table] is not JObject b)return false;
    var changes=steps.Cast<JObject>().Where(s=>s.Value<string>("op")=="set_text_style_font").ToArray();
    if(changes.Select(s=>s.Value<string>("name")).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=changes.Length)return false;
    foreach(var row in a.Properties())
    {var change=table=="text_styles"?changes.SingleOrDefault(s=>s.Value<string>("name")==row.Name):null;
     if(change!=null){if(!VerifyFontChange(row.Value,b[row.Name],change))return false;}
     else if(!JToken.DeepEquals(row.Value,b[row.Name])&&!(table=="text_styles"&&steps.Count==1&&steps[0].Value<string>("op")=="save"&&TextStyleSaveEquivalent(row.Value,b[row.Name])))return false;}
    if(table=="text_styles"&&changes.Any(s=>a[s.Value<string>("name")]==null))return false;
    var added=b.Properties().Select(p=>p.Name).Except(a.Properties().Select(p=>p.Name)).ToHashSet();
    string op=table=="text_styles"?"text_style":"dimension_style";
    if(!added.SetEquals(steps.Cast<JObject>().Where(s=>s.Value<string>("op")==op&&!a.ContainsKey(s.Value<string>("name"))).Select(s=>s.Value<string>("name"))))return false;
   }
   if(old["block_definitions"] is JObject blocks){if(next["block_definitions"] is not JObject newBlocks)return false;foreach(var row in blocks.Properties())if(!Equivalent(FontGeometryNeutral(row.Value,steps),FontGeometryNeutral(newBlocks[row.Name],steps)))return false;}
   return true;
  }
  static JToken FontGeometryNeutral(JToken value,JArray steps)
  {
   if(value==null)return JValue.CreateNull();var result=value.DeepClone();
   var names=steps.Cast<JObject>().Where(s=>s.Value<string>("op")=="set_text_style_font").Select(s=>s.Value<string>("name")).ToHashSet();
   void Walk(JToken node){if(node is JObject o){if(o.Value<string>("type") is "DBText" or "MText"&&names.Contains(o.Value<string>("text_style")))foreach(string f in new[]{"extents","actual_width","actual_height"})o.Remove(f);foreach(var p in o.Properties().ToArray())Walk(p.Value);}else if(node is JArray a)foreach(var n in a)Walk(n);}
   Walk(result);return result;
  }
  static bool VerifyFontChange(JToken before,JToken after,JObject step)
  {
   if(before is not JObject a||after is not JObject b||Hash(a)!=step.Value<string>("expected_sha256")||!string.Equals(b.Value<string>("font_file"),step.Value<string>("font_file"),StringComparison.OrdinalIgnoreCase)||!string.Equals(b.Value<string>("big_font_file"),step.Value<string>("big_font_file"),StringComparison.OrdinalIgnoreCase))return false;
   var x=(JObject)a.DeepClone();var y=(JObject)b.DeepClone();foreach(string f in new[]{"font_file","big_font_file","resolved_font"}){x.Remove(f);y.Remove(f);}return Equivalent(x,y);
  }
  public static bool TextStyleSaveEquivalent(JToken before,JToken after)
  {
   if(before is not JObject a||after is not JObject b||a["resolved_font"] is not JObject font||string.IsNullOrWhiteSpace(font.Value<string>("typeface"))||!JToken.DeepEquals(font,b["resolved_font"]))return false;
   string oldFile=a.Value<string>("font_file")??"",newFile=b.Value<string>("font_file")??"";
   if(!(oldFile.Length==0||oldFile.EndsWith(".ttf",StringComparison.OrdinalIgnoreCase))||!newFile.EndsWith(".ttf",StringComparison.OrdinalIgnoreCase))return false;
   var x=(JObject)a.DeepClone();var y=(JObject)b.DeepClone();x.Remove("font_file");y.Remove("font_file");return JToken.DeepEquals(x,y);
  }
  public static bool VerifyExtension(JObject step,JObject receipt,out bool handled)
  {
   if(BlockOperations.Contains(step.Value<string>("op")))return VerifyBlock(step,receipt,out handled);
   if(CurveOperations.Contains(step.Value<string>("op")))return VerifyCurve(step,receipt,out handled);
   if(PrintOperations.Contains(step.Value<string>("op")))return VerifyPrint(step,receipt,out handled);
   if(Phase2Operations.Contains(step.Value<string>("op")))return VerifyPhase2(step,receipt,out handled);
   handled=true;if(step.Value<string>("op")=="angular_dimension")return VerifyAngular(step,receipt);if(step.Value<string>("op")=="export_dxf")return VerifyDxf(step,receipt);var op=step.Value<string>("op");var before=(JObject)receipt["before"];var after=(JObject)receipt["after"];var map=receipt["objects"]?[step.Value<string>("id")] as JObject;
   if(map==null)return false;
   if(op=="set_text_style_font")return map.Value<string>("name")==step.Value<string>("name")&&VerifyFontChange(receipt["symbols_before"]?["text_styles"]?[step.Value<string>("name")],receipt["symbols_after"]?["text_styles"]?[step.Value<string>("name")],step);
   if(op is "text_style" or "dimension_style")
   {
    var table=op=="text_style"?"text_styles":"dimension_styles";var facts=receipt["symbols_after"]?[table]?[step.Value<string>("name")] as JObject;
    if(facts==null||map.Value<string>("name")!=step.Value<string>("name"))return false;
    foreach(var p in step.Properties().Where(p=>p.Name is not ("id" or "op" or "name")))if(!Equivalent(p.Value,facts[p.Name],p.Name))return false;
    return true;
   }
   if(op=="set_layer")return map.Value<string>("layer")==step.Value<string>("name");
   if(op=="set_callout_format")
   {
    string lh=step.Value<string>("handle"),th=step.Value<string>("text_handle");var l=after[lh] as JObject;var t=after[th] as JObject;
    if(l==null||t==null||Hash(before[lh])!=step.Value<string>("expected_sha256")||Hash(before[th])!=step.Value<string>("text_expected_sha256")||l.Value<string>("type")!="Leader"||t.Value<string>("type")!="MText"||l.Value<string>("annotation_handle")!=th||l.Value<string>("style_name")!=step.Value<string>("dimension_style")||t.Value<string>("text_style")!=step.Value<string>("text_style")||l.Value<int?>("color_index")!=step.Value<int>("leader_color")||t.Value<int?>("color_index")!=step.Value<int>("text_color")||!Near(t["text_height"],Number(step["height"]))||!Near(t["width"],Number(step["width"]))||!Near(l["text_height"],Number(step["height"]))||!Near(l["gap"],Number(step["gap"])))return false;
    if(step["attachment"]!=null&&t.Value<string>("attachment")!=step.Value<string>("attachment"))return false;
    foreach(var row in new[]{(lh,new[]{"color_index","style_name","text_height","gap","extents"}),(th,new[]{"color_index","text_style","text_height","width","actual_width","actual_height","extents"})})
    {var a=(JObject)before[row.Item1].DeepClone();var b=(JObject)after[row.Item1].DeepClone();foreach(var field in row.Item2){a.Remove(field);b.Remove(field);}if(row.Item1==th&&step["attachment"]!=null){a.Remove("attachment");b.Remove("attachment");}if(!Equivalent(a,b))return false;}
    return map.Value<string>("leader")==lh&&map.Value<string>("text")==th;
   }
   if(op is "rotate" or "scale" or "copy")
   {
    var targets=((JArray)step["targets"]).Cast<JObject>().ToArray();
    if(map.Properties().Count()!=targets.Length)return false;
    foreach(var t in targets)
    {
     var h=t.Value<string>("handle");var old=before[h] as JObject;if(old==null||Hash(old)!=t.Value<string>("expected_sha256"))return false;
     var nh=map.Value<string>(h);if(string.IsNullOrEmpty(nh))return false;
     if(op=="copy"&&(!Equivalent(old,after[h])||nh==h))return false;
     if(op!="copy"&&nh!=h)return false;
     var expected=TransformFacts(old,step);
     if(op=="copy"){expected["handle"]=nh;RemapReferences(expected,map);}
     var actual=after[nh] as JObject;if(actual==null||!TransformEquivalent(expected,actual))return false;
     if(actual.Value<string>("type")=="Hatch")
     {
      if(!(receipt.Value<string>("verification_scope")??"").EndsWith(";cached_hatch_area_requires_independent_read",StringComparison.Ordinal))return false;
      string boundary=actual["loops"]?[0]?["associated_handles"]?[0]?.Value<string>();
      if(boundary==null||!HatchMatches(actual,after[boundary] as JObject,false))return false;
     }
    }
    return true;
   }
   if(op is "linear_dimension" or "aligned_dimension" or "radial_dimension" or "diameter_dimension")
   {
    var e=after[map.Value<string>("entity")??""] as JObject;
    string expectedType=op=="linear_dimension"?"RotatedDimension":op=="aligned_dimension"?"AlignedDimension":op=="radial_dimension"?"RadialDimension":"DiametricDimension";
    if(e==null||e.Value<string>("type")!=expectedType||!EntityOptions(e,step)||e.Value<string>("dimension_kind")!=op||e.Value<string>("dimension_style")!=step.Value<string>("dimension_style")||e.Value<string>("dimension_text")!="")return false;
    double measurement;
    if(op is "linear_dimension" or "aligned_dimension")
    {
     if(!PointMatches(e["start"],(JArray)step["start"])||!PointMatches(e["end"],(JArray)step["end"])||e["dimension_line_point"] is not JObject nativeLine)return false;
     double angle=step.Value<double?>("rotation")??0;
     if(op=="linear_dimension"&&!Near(e["rotation"],NormalizeAngle(angle*Math.PI/180),1e-10))return false;
     double direction=op=="linear_dimension"?angle*Math.PI/180:Math.Atan2(Number(step["end"][1])-Number(step["start"][1]),Number(step["end"][0])-Number(step["start"][0]));
     double offsetError=-(Number(nativeLine["x"])-Number(step["position"][0]))*Math.Sin(direction)+(Number(nativeLine["y"])-Number(step["position"][1]))*Math.Cos(direction);
     if(Math.Abs(offsetError)>0.001||!Near(nativeLine["z"],0))return false;
     measurement=op=="aligned_dimension"?Distance(step["start"],step["end"]):Math.Abs((Number(step["end"][0])-Number(step["start"][0]))*Math.Cos(angle*Math.PI/180)+(Number(step["end"][1])-Number(step["start"][1]))*Math.Sin(angle*Math.PI/180));
    }
    else
    {
     if(!PointMatches(e["center"],(JArray)step["center"])||!PointMatches(e["chord"],(JArray)step["chord"]))return false;
     double radius=Distance(step["center"],step["chord"]),offset=Number(step["text_offset"]);
     var position=new JArray(Number(step["chord"][0])+(Number(step["chord"][0])-Number(step["center"][0]))*offset/radius,Number(step["chord"][1])+(Number(step["chord"][1])-Number(step["center"][1]))*offset/radius);
     if(!PointMatches(e["text_position"],position)||e.Value<bool?>("using_default_text_position")!=false)return false;
     measurement=Distance(step["center"],step["chord"])*(op=="diameter_dimension"?2:1);
    }
    var style=receipt["symbols_after"]?["dimension_styles"]?[step.Value<string>("dimension_style")];
    if(style==null||!Near(e["measurement"],measurement))return false;
    foreach(var f in new[]{"text_height","arrow_size","overall_scale","gap","decimal_places","dimension_color","extension_color","text_color"})if(!Equivalent(e[f],style[f],f))return false;
    return true;
   }
   handled=false;return true;
  }
  public static JObject TransformFacts(JObject source,JObject step)
  {
   var value=(JObject)source.DeepClone();var type=value.Value<string>("type");
   if(!new[]{"Line","Circle","DBText","Text","MText","Leader","Polyline","Hatch"}.Contains(type))throw new ArgumentException("unsupported transform type");
   var op=step.Value<string>("op");double factor=op=="scale"?Number(step["factor"]):1,angle=op=="rotate"?Number(step["angle"])*Math.PI/180:0;
   JObject P(JToken token)
   {
    var p=(JObject)token.DeepClone();double x=Number(p["x"]),y=Number(p["y"]);
    if(op=="copy"){x+=Number(step["delta"][0]);y+=Number(step["delta"][1]);}
    else{double bx=Number(step["base"][0]),by=Number(step["base"][1]);double dx=x-bx,dy=y-by;x=bx+factor*(dx*Math.Cos(angle)-dy*Math.Sin(angle));y=by+factor*(dx*Math.Sin(angle)+dy*Math.Cos(angle));}
    p["x"]=x;p["y"]=y;
    foreach(string width in new[]{"start_width","end_width"})if(p[width]!=null&&p[width].Type!=JTokenType.Null)p[width]=Number(p[width])*factor;
    return p;
   }
   foreach(string f in new[]{"start","end","center","position","location"})if(value[f] is JObject)value[f]=P(value[f]);
   foreach(string f in new[]{"vertices","vertices_wcs"})if(value[f] is JArray vs)value[f]=new JArray(vs.Select(P));
   foreach(string f in new[]{"radius","diameter","circumference","length","height","text_height","width","actual_width","actual_height","constant_width"})if(value[f]!=null&&value[f].Type!=JTokenType.Null)value[f]=Number(value[f])*factor;
   if(type=="Leader"&&value["gap"]!=null)value["gap"]=Number(value["gap"])*factor;
   if(value["area"]!=null)value["area"]=Number(value["area"])*factor*factor;
   if(value["rotation"]!=null)value["rotation"]=NormalizeAngle(Number(value["rotation"])+angle);
   return value;
  }
  public static double NormalizeAngle(double a){a%=2*Math.PI;return a<0?a+2*Math.PI:a;}
  static bool TransformEquivalent(JObject expected,JObject actual)
  {
   var a=(JObject)expected.DeepClone();var b=(JObject)actual.DeepClone();a.Remove("extents");b.Remove("extents");
   if(a.Value<string>("type")=="Hatch")foreach(string f in new[]{"loops","area"}){a.Remove(f);b.Remove(f);}
   foreach(string f in new[]{"rotation"})if(a[f]!=null&&b[f]!=null){a[f]=NormalizeAngle(Number(a[f]));b[f]=NormalizeAngle(Number(b[f]));}
   return Equivalent(a,b);
  }
  static bool VerifyCalloutDot(JObject step,JObject before,JObject after,JObject oldLeader,JObject nextLeader)
  {
   if(step["dot_companions"] is not JArray targets)return oldLeader.Value<bool?>("has_arrow_head")!=false||PointToPoint(oldLeader["vertices_wcs"][0],nextLeader["vertices_wcs"][0]);
   foreach(JObject target in targets)if(Hash(before[target.Value<string>("handle")])!=target.Value<string>("expected_sha256"))return false;
   string circle=targets.Cast<JObject>().Select(t=>t.Value<string>("handle")).SingleOrDefault(h=>before[h]?.Value<string>("type")=="Circle");
   string hatch=targets.Cast<JObject>().Select(t=>t.Value<string>("handle")).SingleOrDefault(h=>before[h]?.Value<string>("type")=="Hatch");
   if(circle==null||hatch==null||!PointToPoint(before[circle]["center"],oldLeader["vertices_wcs"][0])||!PointToPoint(after[circle]["center"],nextLeader["vertices_wcs"][0])||!HatchMatches((JObject)after[hatch],(JObject)after[circle],false))return false;
   foreach(string h in new[]{circle,hatch})
   {var a=(JObject)before[h].DeepClone();var b=(JObject)after[h].DeepClone();foreach(string field in h==circle?new[]{"center","extents"}:new[]{"loops","extents"}){a.Remove(field);b.Remove(field);}if(!Equivalent(a,b))return false;}
   return true;
  }
  static void RemapReferences(JToken value,JObject mapping)
  {
   if(value is JObject obj)foreach(var p in obj.Properties().ToArray())
   {
    if(p.Name=="annotation_handle"&&p.Value.Type==JTokenType.String&&mapping[p.Value.Value<string>()]!=null)p.Value=mapping[p.Value.Value<string>()].DeepClone();
    else if(p.Name=="associated_handles"&&p.Value is JArray handles)foreach(var h in handles.ToArray())if(mapping[h.Value<string>()]!=null)h.Replace(mapping[h.Value<string>()].DeepClone());
    else RemapReferences(p.Value,mapping);
   }
   else if(value is JArray array)foreach(var item in array)RemapReferences(item,mapping);
  }
 }
}
