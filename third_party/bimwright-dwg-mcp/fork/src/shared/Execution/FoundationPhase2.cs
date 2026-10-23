// Bounded, backend-independent phase-two contracts. capskov 2026, Apache-2.0.
using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Shared.Execution
{
 public static partial class FoundationDrawingPlan
 {
  public static string[] Phase2Operations=>new[]{"point","arc","ellipse","rectangle","set_properties","set_open_polyline","set_text_format","set_text_metrics","set_dimension_text","table","set_table_cell","set_line","set_circle","set_hatch_pattern","set_raster_path"};
  static bool ValidatePhase2(JObject s,ref int count)
  {
   string op=s.Value<string>("op");
   switch(op)
   {
    case "set_raster_path":
     Keys(s,"id","op","targets","path");ValidateTargets(s,ref count);
     if(((JArray)s["targets"]).Count!=1||s["path"].Type!=JTokenType.String||!System.IO.Path.IsPathFullyQualified(s.Value<string>("path"))||s.Value<string>("path").Length>1024||s.Value<string>("path").Any(char.IsControl)||new[]{".dwg",".dxf",".exe",".dll"}.Contains(System.IO.Path.GetExtension(s.Value<string>("path")).ToLowerInvariant()))throw new ArgumentException("one raster reference and explicit absolute image path required");break;
    case "set_text_metrics":
     Keys(s,"id","op","targets","height_scale","width_scale");ValidateTargets(s,ref count);
     foreach(string key in new[]{"height_scale","width_scale"}){Positive(s,key);if(Number(s[key])<0.01||Number(s[key])>100)throw new ArgumentException("text metric scale requires 0.01..100");}break;
    case "point":Keys(s,"id","op","position","layer","color");Point(s["position"]);Options(s);count++;break;
    case "table":
     Keys(s,"id","op","position","cells","row_height","column_width","text_height","text_style","layer","color");Point(s["position"]);Options(s);Positive(s,"row_height");Positive(s,"column_width");Positive(s,"text_height");Text(s,"text_style",255);
     if(s["cells"] is not JArray rows||rows.Count<1||rows.Count>16||rows[0] is not JArray first||first.Count<1||first.Count>16||rows.Count*first.Count>128)throw new ArgumentException("table requires 1..16 rows/columns, at most 128 cells");
     foreach(var row in rows){if(row is not JArray columns||columns.Count!=first.Count)throw new ArgumentException("rectangular cells required");foreach(var cell in columns)if(cell.Type!=JTokenType.String||cell.Value<string>().Length>256||cell.Value<string>().Contains('\0'))throw new ArgumentException("bounded cell text required");}count++;break;
    case "set_table_cell":
     Keys(s,"id","op","targets","row","column","text");ValidateTargets(s,ref count);
     if(((JArray)s["targets"]).Count!=1||s["row"]?.Type!=JTokenType.Integer||s["column"]?.Type!=JTokenType.Integer||s.Value<int>("row")<0||s.Value<int>("column")<0||s.Value<int>("row")>=16||s.Value<int>("column")>=16||s["text"]?.Type!=JTokenType.String||s.Value<string>("text").Length>256||s.Value<string>("text").Contains('\0'))throw new ArgumentException("one bounded table cell required");break;
    case "arc":
     Keys(s,"id","op","center","radius","start_angle","sweep_angle","layer","color");Point(s["center"]);Positive(s,"radius");Number(s["start_angle"]);Options(s);
     double sweep=Number(s["sweep_angle"]);if(sweep<=0||sweep>=360)throw new ArgumentException("arc sweep_angle must be >0 and <360 degrees");count++;break;
    case "ellipse":
     Keys(s,"id","op","center","major_axis","radius_ratio","layer","color");Point(s["center"]);Point(s["major_axis"]);Options(s);
     if(Distance(s["major_axis"],new JArray(0,0))<=1e-9||Number(s["radius_ratio"])<=0||Number(s["radius_ratio"])>1)throw new ArgumentException("nonzero major_axis and radius_ratio in (0,1] required");count++;break;
    case "rectangle":
     Keys(s,"id","op","min","max","layer","color");Point(s["min"]);Point(s["max"]);Options(s);
     if(Number(s["max"][0])<=Number(s["min"][0])||Number(s["max"][1])<=Number(s["min"][1]))throw new ArgumentException("rectangle max must exceed min on both axes");count++;break;
    case "set_properties":
     KeysWithOptional(s,new[]{"id","op","targets","layer","color"},"linetype","lineweight");Text(s,"layer",255);Color(s,"color");ValidateTargets(s,ref count);
     if(s["linetype"]!=null)Text(s,"linetype",255);
     if(s["lineweight"]!=null&&(s["lineweight"].Type!=JTokenType.Integer||!new[]{-3,-2,-1,0,5,9,13,15,18,20,25,30,35,40,50,53,60,70,80,90,100,106,120,140,158,200,211}.Contains(s.Value<int>("lineweight"))))throw new ArgumentException("native lineweight enum in hundredths of a millimetre required");break;
    case "set_hatch_pattern":
     Keys(s,"id","op","targets","hatch_handle","pattern","scale","angle");ValidateTargets(s,ref count);Text(s,"hatch_handle",16);Text(s,"pattern",128);Positive(s,"scale");Number(s["angle"]);
     if(((JArray)s["targets"]).Count!=2||!((JArray)s["targets"]).Cast<JObject>().Any(t=>t.Value<string>("handle")==s.Value<string>("hatch_handle")))throw new ArgumentException("hatch and its single boundary required");break;
    case "set_line":case "set_circle":
     if(op=="set_line"){Keys(s,"id","op","targets","start","end");Point(s["start"]);Point(s["end"]);if(Distance(s["start"],s["end"])<=1e-9)throw new ArgumentException("nonzero line required");}
     else{Keys(s,"id","op","targets","center","radius");Point(s["center"]);Positive(s,"radius");}
     ValidateTargets(s,ref count);if(((JArray)s["targets"]).Count!=1)throw new ArgumentException("one standalone primitive required");break;
    case "set_open_polyline":
     Keys(s,"id","op","targets","vertices");ValidateTargets(s,ref count);
     if(((JArray)s["targets"]).Count!=1||s["vertices"] is not JArray v||v.Count<2||v.Count>128)throw new ArgumentException("one open polyline and 2..128 vertices required");
     foreach(var p in v){Point(p,true);if(((JArray)p).Count==3&&Math.Abs(Number(p[2]))>1)throw new ArgumentException("major bulge unsupported");}break;
    case "set_text_format":
     KeysWithOptional(s,new[]{"id","op","targets","position","height","rotation","text_style"},"width");ValidateTargets(s,ref count);Point(s["position"]);Positive(s,"height");Number(s["rotation"]);Text(s,"text_style",255);if(s["width"]!=null)Positive(s,"width");
     if(((JArray)s["targets"]).Count!=1)throw new ArgumentException("one standalone text required");break;
    case "set_dimension_text":
     Keys(s,"id","op","targets","text","position","height");ValidateTargets(s,ref count);Point(s["position"]);Positive(s,"height");
     if(((JArray)s["targets"]).Count!=1||s["text"]?.Type!=JTokenType.String||s.Value<string>("text").Length>2048||s.Value<string>("text").Contains('\0'))throw new ArgumentException("one dimension and bounded text override required (empty resets override)");break;
    default:return false;
   }
   return true;
  }
  static void ValidateTargets(JObject s,ref int count)
  {
   if(s["targets"] is not JArray ts||ts.Count<1||ts.Count>32)throw new ArgumentException("1..32 targets required");
   foreach(JObject t in ts){Keys(t,"handle","expected_sha256");Expected(t);}
   if(ts.Cast<JObject>().Select(t=>t.Value<string>("handle")).Distinct().Count()!=ts.Count)throw new ArgumentException("duplicate target");count+=ts.Count;
  }
  static bool VerifyPhase2(JObject s,JObject receipt,out bool handled)
  {
   string op=s.Value<string>("op");handled=Phase2Operations.Contains(op);if(!handled)return true;
   var before=(JObject)receipt["before"];var after=(JObject)receipt["after"];var map=receipt["objects"]?[s.Value<string>("id")] as JObject;if(map==null)return false;
   if(op is "point" or "arc" or "ellipse" or "rectangle")
   {
    var e=after[map.Value<string>("entity")??""] as JObject;if(e==null||!EntityOptions(e,s))return false;
    if(op=="point")return e.Value<string>("type")=="DBPoint"&&PointMatches(e["position"],(JArray)s["position"]);
    if(op=="arc")return e.Value<string>("type")=="Arc"&&PointMatches(e["center"],(JArray)s["center"])&&Near(e["radius"],Number(s["radius"]))&&Near(e["start_angle"],NormalizeAngle(Number(s["start_angle"])*Math.PI/180),1e-9)&&Near(e["included_angle"],Number(s["sweep_angle"])*Math.PI/180,1e-9);
    if(op=="ellipse")return e.Value<string>("type")=="Ellipse"&&PointMatches(e["center"],(JArray)s["center"])&&PointMatches(e["major_axis"],(JArray)s["major_axis"])&&Near(e["radius_ratio"],Number(s["radius_ratio"]),1e-10)&&Near(e["start_angle"],0,1e-9)&&Near(e["end_angle"],2*Math.PI,1e-9);
    var min=(JArray)s["min"];var max=(JArray)s["max"];return e.Value<string>("type")=="Polyline"&&e.Value<bool?>("is_closed")==true&&VerticesMatch(e["vertices"],new JArray(min.DeepClone(),new JArray(max[0],min[1]),max.DeepClone(),new JArray(min[0],max[1])))&&Near(e["area"],(Number(max[0])-Number(min[0]))*(Number(max[1])-Number(min[1])));
   }
   if(op=="table")
   {
    var e=after[map.Value<string>("entity")??""] as JObject;var cells=(JArray)s["cells"];
    if(e==null||e.Value<string>("type")!="Table"||!EntityOptions(e,s)||!PointMatches(e["position"],(JArray)s["position"])||e.Value<bool?>("cells_complete")!=true||e.Value<int?>("rows")!=cells.Count||e.Value<int?>("columns")!=((JArray)cells[0]).Count||!JToken.DeepEquals(e["cells"],cells))return false;
    if(e["row_heights"] is not JArray heights||heights.Any(v=>!Near(v,Number(s["row_height"])))||e["column_widths"] is not JArray widths||widths.Any(v=>!Near(v,Number(s["column_width"]))))return false;
    return e["cell_text_heights"] is JArray th&&th.SelectMany(v=>(JArray)v).All(v=>Near(v,Number(s["text_height"])))&&e["cell_text_styles"] is JArray styles&&styles.SelectMany(v=>(JArray)v).All(v=>v.Value<string>()==s.Value<string>("text_style"));
   }
   var targets=(JArray)s["targets"];if(map.Count!=targets.Count)return false;
   foreach(JObject t in targets)
   {
    string h=t.Value<string>("handle");var old=before[h] as JObject;var current=after[h] as JObject;
    if(old==null||current==null||Hash(old)!=t.Value<string>("expected_sha256")||map.Value<string>(h)!=h)return false;
    var a=(JObject)old.DeepClone();var b=(JObject)current.DeepClone();string[] mutable;
    if(op=="set_raster_path")
    {if(old.Value<string>("type")!="RasterImage"||current.Value<string>("source_file")!=s.Value<string>("path"))return false;mutable=new[]{"source_file"};}
    else if(op=="set_hatch_pattern")
    {
     if(h==s.Value<string>("hatch_handle"))
     {string boundary=targets.Cast<JObject>().Select(v=>v.Value<string>("handle")).Single(v=>v!=h);if(old.Value<string>("type")!="Hatch"||current.Value<string>("pattern_name")!=s.Value<string>("pattern")||!Near(current["pattern_scale"],Number(s["scale"]),1e-10)||!Near(current["pattern_angle"],NormalizeAngle(Number(s["angle"])),1e-10)||!HatchMatches(current,(JObject)after[boundary],false))return false;mutable=new[]{"pattern_name","pattern_scale","pattern_angle","area","area_status","loops","extents"};}
     else{if(!Equivalent(old,current))return false;continue;}
    }
    else if(op=="set_line")
    {if(old.Value<string>("type")!="Line"||!PointMatches(current["start"],(JArray)s["start"])||!PointMatches(current["end"],(JArray)s["end"])||!Near(current["length"],Distance(s["start"],s["end"])))return false;mutable=new[]{"start","end","length","extents"};}
    else if(op=="set_circle")
    {double r=Number(s["radius"]);if(old.Value<string>("type")!="Circle"||!PointMatches(current["center"],(JArray)s["center"])||!Near(current["radius"],r)||!Near(current["diameter"],2*r)||!Near(current["circumference"],2*Math.PI*r)||!Near(current["area"],Math.PI*r*r))return false;mutable=new[]{"center","radius","diameter","circumference","area","extents"};}
    else if(op=="set_table_cell")
    {
     if(old.Value<string>("type")!="Table"||old.Value<bool?>("cells_complete")!=true)return false;
     int row=s.Value<int>("row"),column=s.Value<int>("column");if(row>=old.Value<int>("rows")||column>=old.Value<int>("columns"))return false;
     var expected=(JArray)old["cells"].DeepClone();expected[row][column]=s["text"].DeepClone();if(!JToken.DeepEquals(current["cells"],expected))return false;mutable=new[]{"cells","extents"};
    }
    else if(op=="set_properties")
    {if(!EntityOptions(current,s)||s["linetype"]!=null&&current.Value<string>("linetype")!=s.Value<string>("linetype")||s["lineweight"]!=null&&current.Value<int?>("lineweight")!=s.Value<int>("lineweight"))return false;mutable=new[]{"layer","color_index"}.Concat(s["linetype"]!=null?new[]{"linetype"}:Array.Empty<string>()).Concat(s["lineweight"]!=null?new[]{"lineweight"}:Array.Empty<string>()).ToArray();}
    else if(op=="set_open_polyline")
    {if(old.Value<string>("type")!="Polyline"||old.Value<bool?>("is_closed")!=false||current.Value<bool?>("is_closed")!=false||!VerticesMatch(current["vertices"],(JArray)s["vertices"]))return false;mutable=new[]{"vertices","vertex_count","length","area","extents"};}
    else if(op=="set_text_metrics")
    {
     if(old.Value<string>("type")!="DBText"||!Near(current["height"],Number(old["height"])*Number(s["height_scale"]),1e-9)||!Near(current["width_factor"],Number(old["width_factor"])*Number(s["width_scale"]),1e-9))return false;
     bool left=old.Value<string>("horizontal_mode")=="TextLeft"&&old.Value<string>("vertical_mode")=="TextBase";
     if(!Equivalent(old[left?"position":"alignment_point"],current[left?"position":"alignment_point"]))return false;
     mutable=left?new[]{"height","width_factor","extents"}:new[]{"height","width_factor","position","extents"};
    }
    else if(op=="set_text_format")
    {
     bool mt=old.Value<string>("type")=="MText";if(!mt&&old.Value<string>("type")!="DBText")return false;
     if(!PointMatches(current[mt?"location":"position"],(JArray)s["position"])||!Near(current[mt?"text_height":"height"],Number(s["height"]))||!Near(current["rotation"],NormalizeAngle(Number(s["rotation"])*Math.PI/180),1e-9)||current.Value<string>("text_style")!=s.Value<string>("text_style"))return false;
     if(mt?(s["width"]==null||!Near(current["width"],Number(s["width"]))):s["width"]!=null)return false;
     mutable=new[]{"location","position","text_height","height","rotation","text_style","width","actual_width","actual_height","extents"};
    }
    else
    {
     if(old["dimension_kind"]==null||current.Value<string>("dimension_text")!=s.Value<string>("text")||!PointMatches(current["text_position"],(JArray)s["position"])||current.Value<bool?>("using_default_text_position")!=false||current.Value<int?>("text_movement")!=2||!Near(current["text_height"],Number(s["height"])))return false;
     mutable=new[]{"dimension_text","text_position","using_default_text_position","text_height","text_movement","extents"};
    }
    foreach(string f in mutable){a.Remove(f);b.Remove(f);}if(!Equivalent(a,b))return false;
   }
   return true;
  }
  static bool VerticesMatch(JToken actual,JArray expected)
  {if(actual is not JArray vs||vs.Count!=expected.Count)return false;for(int i=0;i<vs.Count;i++){var p=(JArray)expected[i];if(!PointMatches(vs[i],p)||!Near(vs[i]["bulge"],p.Count==3?Number(p[2]):0,1e-10))return false;}return true;}
 }
}
