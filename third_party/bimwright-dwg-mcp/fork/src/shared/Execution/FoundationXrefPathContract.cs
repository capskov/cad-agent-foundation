using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution {
 public static partial class FoundationDrawingPlan {
  static bool ValidateXrefPath(JObject s) {
   if(s.Value<string>("op")!="set_xref_path")return false;
   Keys(s,"id","op","name","expected_sha256","path","file_sha256");
   Text(s,"name",255);Digest(s,"expected_sha256");Digest(s,"file_sha256");
   if(!Regex.IsMatch(Text(s,"path",255),@"\A[^/\\:\x00-\x1f]+\.dwg\z",RegexOptions.IgnoreCase))throw new ArgumentException("explicit DWG basename beside the owned drawing required");
   return true;
  }
  static bool VerifyXrefPathChanges(JObject before,JObject after,JArray steps) {
   var changes=steps.Cast<JObject>().Where(s=>s.Value<string>("op")=="set_xref_path").ToArray();
   if(before==null||after==null)return before==null&&after==null&&changes.Length==0;
   if(before.Count!=after.Count||changes.Length>1)return false;
   if(changes.Length>0&&steps.Count!=1)return false;
   foreach(var row in before.Properties()) {
    var change=changes.SingleOrDefault(s=>s.Value<string>("name")==row.Name);
    if(change==null){if(!JToken.DeepEquals(row.Value,after[row.Name]))return false;continue;}
    if(Hash(row.Value)!=change.Value<string>("expected_sha256")||after[row.Name]?.Value<string>("path")!=change.Value<string>("path"))return false;
    var a=(JObject)row.Value.DeepClone();var b=(JObject)after[row.Name].DeepClone();a.Remove("path");b.Remove("path");if(!JToken.DeepEquals(a,b))return false;
   }
   return changes.All(s=>before[s.Value<string>("name")]!=null);
  }
 }
}
