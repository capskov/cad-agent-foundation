using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution
{
 public static partial class FoundationDrawingPlan
 {
  public static string[] BlockOperations=>new[]{"block_define","block_insert","set_block_attributes"};
  static bool ValidateBlock(JObject s,ref int count)
  {
   string op=s.Value<string>("op");if(!BlockOperations.Contains(op))return false;
   if(op=="block_define")
   {
    Keys(s,"id","op","name","members");if(!Regex.IsMatch(Text(s,"name",64),"^[A-Za-z][A-Za-z0-9_-]*$"))throw new ArgumentException("simple named block required");
    if(s["members"] is not JArray members||members.Count<1||members.Count>32)throw new ArgumentException("1..32 simple members required");
    foreach(JObject m in members)
    {
     string kind=Text(m,"kind",32);
     if(kind=="line"){Keys(m,"kind","start","end");Point(m["start"]);Point(m["end"]);if(Distance(m["start"],m["end"])<=1e-9)throw new ArgumentException("nonzero block line required");}
     else if(kind=="circle"){Keys(m,"kind","center","radius");Point(m["center"]);Positive(m,"radius");}
     else if(kind=="attribute"){Keys(m,"kind","tag","text","position","height");if(!Regex.IsMatch(Text(m,"tag",32),"^[A-Z][A-Z0-9_]*$"))throw new ArgumentException("uppercase attribute tag required");Text(m,"text",256);Point(m["position"]);Positive(m,"height");}
     else throw new ArgumentException("block member kind must be line,circle,attribute");
    }
    var tags=members.Cast<JObject>().Where(m=>m.Value<string>("kind")=="attribute").Select(m=>m.Value<string>("tag")).ToArray();if(tags.Distinct().Count()!=tags.Length||tags.Length>16)throw new ArgumentException("at most 16 unique attribute tags");count+=members.Count;
   }
   else if(op=="block_insert")
   {
    Keys(s,"id","op","name","definition_sha256","position","scale","rotation","attributes","layer","color");Text(s,"name",64);Digest(s,"definition_sha256");Point(s["position"]);Positive(s,"scale");Number(s["rotation"]);Options(s);Attributes(s);count+=17;
   }
   else{Keys(s,"id","op","targets","attributes");ValidateTargets(s,ref count);if(((JArray)s["targets"]).Count!=1)throw new ArgumentException("one block reference required");Attributes(s);}
   return true;
  }
  static void Attributes(JObject s)
  {if(s["attributes"] is not JObject attrs||attrs.Count>16)throw new ArgumentException("0..16 named attributes required");foreach(var p in attrs.Properties())if(!Regex.IsMatch(p.Name,"^[A-Z][A-Z0-9_]{0,31}$")||p.Value.Type!=JTokenType.String||p.Value.Value<string>().Length>256||p.Value.Value<string>().Contains('\0'))throw new ArgumentException("bounded uppercase tag and text required");}
  static bool VerifyBlock(JObject s,JObject receipt,out bool handled)
  {
   string op=s.Value<string>("op");handled=BlockOperations.Contains(op);if(!handled)return true;
   var old=receipt["symbols_before"]?["block_definitions"] as JObject;var next=receipt["symbols_after"]?["block_definitions"] as JObject;if(old==null||next==null)return false;
   foreach(var row in old.Properties())if(!Equivalent(row.Value,next[row.Name]))return false;
   var map=receipt["objects"]?[s.Value<string>("id")] as JObject;if(map==null)return false;
   if(op=="block_define")
   {
    string name=s.Value<string>("name");if(old[name]!=null||map.Value<string>("name")!=name||next[name] is not JObject def||def.Value<bool?>("complete")!=true)return false;
    var members=(JArray)s["members"];var actual=def["members"] as JArray;if(actual==null||members.Count!=actual.Count)return false;
    for(int i=0;i<members.Count;i++)
    {
     var m=(JObject)members[i];var a=(JObject)actual[i];string kind=m.Value<string>("kind");
     if(kind=="line"&&(a.Value<string>("type")!="Line"||!PointMatches(a["start"],(JArray)m["start"])||!PointMatches(a["end"],(JArray)m["end"])))return false;
     if(kind=="circle"&&(a.Value<string>("type")!="Circle"||!PointMatches(a["center"],(JArray)m["center"])||!Near(a["radius"],Number(m["radius"]))))return false;
     if(kind=="attribute"&&(a.Value<string>("type")!="AttributeDefinition"||a.Value<string>("attribute_tag")!=m.Value<string>("tag")||a.Value<string>("text")!=m.Value<string>("text")||!PointMatches(a["position"],(JArray)m["position"])||!Near(a["height"],Number(m["height"]))))return false;
     if(a.Value<string>("layer")!="0"||a.Value<int?>("color_index")!=0)return false;
    }
    return true;
   }
   string h=op=="block_insert"?map.Value<string>("entity"):s["targets"][0].Value<string>("handle");var e=h==null?null:receipt["after"]?[h] as JObject;
   if(e==null||e.Value<string>("type")!="BlockReference"||e.Value<bool?>("attributes_complete")!=true||e["attributes"] is not JArray attrs)return false;
   if(op=="block_insert")
   {
    if(!EntityOptions(e,s)||!PointMatches(e["position"],(JArray)s["position"])||e.Value<string>("name")!=s.Value<string>("name")||old[s.Value<string>("name")]==null||Hash(old[s.Value<string>("name")])!=s.Value<string>("definition_sha256")||!Near(e["rotation"],NormalizeAngle(Number(s["rotation"])*Math.PI/180),1e-9))return false;
    foreach(string axis in new[]{"x","y","z"})if(!Near(e["scale"]?[axis],Number(s["scale"])))return false;
    var definitions=((JArray)old[s.Value<string>("name")]["members"]).Cast<JObject>().Where(m=>m.Value<string>("type")=="AttributeDefinition").ToArray();if(attrs.Count!=definitions.Length)return false;
    foreach(var def in definitions){string tag=def.Value<string>("attribute_tag");var actual=attrs.Cast<JObject>().SingleOrDefault(a=>a.Value<string>("tag")==tag);if(actual==null||actual.Value<string>("text")!=(s["attributes"]?[tag]?.Value<string>()??def.Value<string>("text")))return false;}
   }
   else
   {
    var t=(JObject)s["targets"][0];var before=(JObject)receipt["before"][h];if(Hash(before)!=t.Value<string>("expected_sha256")||map.Value<string>(h)!=h)return false;
    var expected=(JObject)before.DeepClone();foreach(var p in ((JObject)s["attributes"]).Properties()){var a=((JArray)expected["attributes"]).Cast<JObject>().SingleOrDefault(a=>a.Value<string>("tag")==p.Name);if(a==null)return false;a["text"]=p.Value.DeepClone();}expected.Remove("extents");var actual=(JObject)e.DeepClone();actual.Remove("extents");if(!Equivalent(expected,actual))return false;
   }
   return true;
  }
 }
}
