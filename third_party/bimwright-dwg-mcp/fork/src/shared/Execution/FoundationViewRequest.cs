using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution
{
 public static class FoundationViewRequest
 {
  public const string Mcp="dwg_view", Plugin="foundation_view";
  public static JObject Parse(string json)
  {
   if(json==null||json.Length>4096)throw new ArgumentException("view request exceeds 4096 characters");
   var p=JObject.Parse(json,new JsonLoadSettings{DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error});
   string action=p.Value<string>("action");
   string[] required=action switch {"read"=>new[]{"action"},"capture"=>new[]{"action","file_name"},"window"=>new[]{"action","min_x","min_y","max_x","max_y"},"center"=>new[]{"action","x","y","height"},_=>throw new ArgumentException("action must be read, window, center or capture")};
   foreach(var key in required)if(p[key]==null)throw new ArgumentException("missing view field: "+key);
   foreach(var prop in p.Properties())if(!required.Contains(prop.Name)&&!new[]{"file_name","pixel_size"}.Contains(prop.Name))throw new ArgumentException("unexpected view field: "+prop.Name);
   foreach(string key in required.Where(k=>k!="action"&&k!="file_name")){
    if(p[key].Type is not (JTokenType.Float or JTokenType.Integer)||!double.IsFinite(p.Value<double>(key)))throw new ArgumentException("finite numeric view field required: "+key);
   }
   if(action=="window"&&(p.Value<double>("max_x")<=p.Value<double>("min_x")||p.Value<double>("max_y")<=p.Value<double>("min_y")))throw new ArgumentException("ordered nonzero WCS window required");
   if(action=="center"&&p.Value<double>("height")<=0)throw new ArgumentException("positive view height required");
   if(p["file_name"]!=null&&(p["file_name"].Type!=JTokenType.String||!Regex.IsMatch(p.Value<string>("file_name"),@"^[A-Za-z0-9][A-Za-z0-9_-]{0,79}\.png$")))throw new ArgumentException("new PNG basename required; output is under owned drawing outputs directory");
   if(p["pixel_size"]!=null&&(p["file_name"]==null||p["pixel_size"].Type!=JTokenType.Integer||p.Value<int>("pixel_size")<64||p.Value<int>("pixel_size")>4096))throw new ArgumentException("pixel_size requires file_name and integer 64..4096");
   return p;
  }
  public static string Canonical(string json)=>FoundationDrawingPlan.Canonical(Parse(json));
  public static bool Valid(string json){try{return Canonical(json)==json;}catch{return false;}}
 }
}
