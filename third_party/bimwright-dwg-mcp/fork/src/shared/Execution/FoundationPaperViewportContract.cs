using System;using System.Linq;using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution {public static partial class FoundationDrawingPlan {
 static bool ValidatePaperViewport(JObject s){if(s.Value<string>("op")=="copy_paper_frame"){Keys(s,"id","op","source_handle","expected_sha256","layout","delta");Expected(new JObject{["handle"]=s["source_handle"],["expected_sha256"]=s["expected_sha256"]});Text(s,"layout",255);Point(s["delta"]);return true;}if(s.Value<string>("op")=="set_paper_text"){Keys(s,"id","op","paper_handle","expected_sha256","layout","text");Expected(new JObject{["handle"]=s["paper_handle"],["expected_sha256"]=s["expected_sha256"]});Text(s,"layout",255);Text(s,"text",2048);return true;}if(s.Value<string>("op")!="set_paper_viewport")return false;KeysWithOptional(s,new[]{"id","op","viewport_handle","expected_sha256","layout","view_center","view_height","locked"},"height_paper","width_paper","layer","center_paper");if(s["center_paper"]!=null)Point(s["center_paper"]);if(s["width_paper"]!=null)Positive(s,"width_paper");if(s["height_paper"]!=null)Positive(s,"height_paper");if(s["layer"]!=null)Text(s,"layer",255);Expected(new JObject{["handle"]=s["viewport_handle"],["expected_sha256"]=s["expected_sha256"]});Text(s,"layout",255);Point(s["view_center"]);Positive(s,"view_height");Bool(s,"locked");return true;}
 static bool VerifyPaperBlockChanges(JObject old,JObject next,JArray steps,JObject receipt){var changes=steps.Cast<JObject>().Where(s=>s.Value<string>("op")=="copy_paper_frame").ToArray();if(old==null||next==null)return old==null&&next==null&&changes.Length==0;if(changes.Length>1||next.Count!=old.Count+changes.Length)return false;foreach(var row in old.Properties())if(!Equivalent(row.Value,next[row.Name]))return false;
 foreach(var c in changes){string h=receipt["objects"]?[c.Value<string>("id")]?.Value<string>("entity");if(string.IsNullOrEmpty(h)||old[h]!=null||next[h] is not JObject actual||old[c.Value<string>("source_handle")] is not JObject source||Hash(source)!=c.Value<string>("expected_sha256")||source.Value<string>("layout_name")!=c.Value<string>("layout")||source.Value<int>("attribute_count")!=0)return false;var expected=(JObject)source.DeepClone();expected["handle"]=h;foreach(string field in new[]{"position"}){expected[field]["x"]=Number(source[field]["x"])+Number(c["delta"][0]);expected[field]["y"]=Number(source[field]["y"])+Number(c["delta"][1]);}var entity=(JObject)receipt["after"]?[h]?.DeepClone();if(entity==null)return false;entity.Remove("extents");var observed=(JObject)actual.DeepClone();observed.Remove("visible");if(!Equivalent(expected,actual)||!Equivalent(observed,entity))return false;}
 return true;}
 static bool VerifyPaperTextChanges(JObject old,JObject next,JArray steps,JObject receipt)
 {
  var changes=steps.Cast<JObject>().Where(s=>s.Value<string>("op")=="set_paper_text").ToArray();
  var additions=steps.Cast<JObject>().Where(s=>s["paper_layout"]!=null && s.Value<string>("op") is "text" or "mtext").ToArray();
  if(old==null||next==null)return old==null&&next==null&&changes.Length==0&&additions.Length==0;
  if(next.Count!=old.Count+additions.Length||changes.Select(s=>s.Value<string>("paper_handle")).Distinct().Count()!=changes.Length)return false;
  var handles=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
  foreach(var step in additions)
  {
   string handle=receipt["objects"]?[step.Value<string>("id")]?.Value<string>("entity");
   if(string.IsNullOrEmpty(handle)||!handles.Add(handle)||old[handle]!=null||next[handle] is not JObject actual||
      receipt["after"]?[handle] is not JObject entity||
      !VerifyCreationPlacement(actual,step,receipt)||!EntityOptions(actual,step)||
      !TextMatches(actual,step,step.Value<string>("op")=="mtext")||
      actual.Value<string>("text_style")!=entity.Value<string>("text_style"))return false;
  }
  var moved=steps.Cast<JObject>().Where(s=>s["paper_layout"]!=null && s.Value<string>("op") is "move" or "archive")
   .SelectMany(s=>((JArray)s["targets"]).Select(t=>(handle:t.Value<string>("handle"),step:s)))
   .ToDictionary(pair=>pair.handle,pair=>pair.step,StringComparer.Ordinal);
  foreach(var row in old.Properties())
  {
   if(moved.TryGetValue(row.Name,out var move))
   {
    if(next[row.Name] is not JObject actual || !VerifyCreationPlacement(actual,move,receipt))return false;
    if(move.Value<string>("op")=="archive")
    {
     var expected=(JObject)row.Value.DeepClone();
     expected["layer"]=move["layer"];
     if(!Equivalent(expected,actual))return false;
    }
    else if(!TransformEquivalent(TransformFacts((JObject)row.Value,new JObject{["op"]="copy",["delta"]=move["delta"]}),actual))return false;
    continue;
   }
   var c=changes.SingleOrDefault(s=>s.Value<string>("paper_handle")==row.Name);if(c==null){if(!Equivalent(row.Value,next[row.Name]))return false;continue;}if(Hash(row.Value)!=c.Value<string>("expected_sha256")||row.Value.Value<string>("layout_name")!=c.Value<string>("layout")||next[row.Name]?.Value<string>("text")!=c.Value<string>("text"))return false;var a=(JObject)row.Value.DeepClone();var b=(JObject)next[row.Name].DeepClone();foreach(string f in new[]{"text","extents","actual_width","actual_height"}){a.Remove(f);b.Remove(f);}if(a.Value<string>("type")=="DBText"&&a.Value<string>("horizontal_mode") is "TextCenter" or "TextRight" or "TextMid"){if(!Equivalent(a["alignment_point"],b["alignment_point"]))return false;a.Remove("position");b.Remove("position");}if(!Equivalent(a,b))return false;}return changes.All(c=>old[c.Value<string>("paper_handle")]!=null);}
 static bool VerifyPaperViewportChanges(JObject old,JObject next,JArray steps){var changes=steps.Cast<JObject>().Where(s=>s.Value<string>("op")=="set_paper_viewport").ToArray();if(old==null||next==null)return old==null&&next==null&&changes.Length==0;if(old.Count!=next.Count||changes.Select(s=>s.Value<string>("viewport_handle")).Distinct().Count()!=changes.Length)return false;
 foreach(var row in old.Properties()){var c=changes.SingleOrDefault(s=>s.Value<string>("viewport_handle")==row.Name);if(c==null){if(!Equivalent(row.Value,next[row.Name]))return false;continue;}if(Hash(row.Value)!=c.Value<string>("expected_sha256")||row.Value.Value<string>("layout_name")!=c.Value<string>("layout")||next[row.Name] is not JObject b)return false;
 if(!Near(b["view_height"],Number(c["view_height"]))||!Equivalent(b["view_center"],c["view_center"])||b.Value<bool>("locked")!=c.Value<bool>("locked")||!Near(b["custom_scale"],Number(b["height_paper"])/Number(c["view_height"]),1e-10))return false;
 var a=(JObject)row.Value.DeepClone();b=(JObject)b.DeepClone();if(c["center_paper"]!=null){double dx=Number(c["center_paper"][0])-Number(a["center_paper"][0]),dy=Number(c["center_paper"][1])-Number(a["center_paper"][1]);a["center_paper"]=new JArray(Number(c["center_paper"][0]),Number(c["center_paper"][1]),Number(a["center_paper"][2]));foreach(string end in new[]{"min","max"}){a["extents"][end]["x"]=Number(a["extents"][end]["x"])+dx;a["extents"][end]["y"]=Number(a["extents"][end]["y"])+dy;}}if(c["layer"]!=null){if(b.Value<string>("layer")!=c.Value<string>("layer"))return false;a["layer"]=c["layer"].DeepClone();}if(c["height_paper"]!=null){double height=Number(c["height_paper"]);if(!Near(b["height_paper"],height))return false;a["height_paper"]=height;a["extents"]["min"]["y"]=Number(a["center_paper"][1])-height/2;a["extents"]["max"]["y"]=Number(a["center_paper"][1])+height/2;}if(c["width_paper"]!=null){double width=Number(c["width_paper"]);if(!Near(b["width_paper"],width))return false;a["width_paper"]=width;a["extents"]["min"]["x"]=Number(a["center_paper"][0])-width/2;a["extents"]["max"]["x"]=Number(a["center_paper"][0])+width/2;}foreach(string f in new[]{"view_height","view_center","custom_scale","locked"}){a.Remove(f);b.Remove(f);}if(!Equivalent(a,b))return false;}
 return changes.All(c=>old[c.Value<string>("viewport_handle")]!=null);}
}}
