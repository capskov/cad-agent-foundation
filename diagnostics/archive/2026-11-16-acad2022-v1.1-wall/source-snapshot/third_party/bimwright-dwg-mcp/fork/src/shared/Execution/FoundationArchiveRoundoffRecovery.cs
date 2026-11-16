using System;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution {
 public static partial class FoundationDrawingPlan {
  public static bool RecoveryExactNumeric(JToken a,JToken b) {
   if(a==null||b==null)return a==null&&b==null;
   if(a is JObject ao&&b is JObject bo)return ao.Count==bo.Count&&ao.Properties().All(p=>bo[p.Name]!=null&&RecoveryExactNumeric(p.Value,bo[p.Name]));
   if(a is JArray aa&&b is JArray ba)return aa.Count==ba.Count&&aa.Zip(ba,RecoveryExactNumeric).All(v=>v);
   if(a.Type is JTokenType.Float or JTokenType.Integer && b.Type is JTokenType.Float or JTokenType.Integer)return Math.Abs(Number(a)-Number(b))<=1e-9;
   return JToken.DeepEquals(a,b);
  }
  public static bool VerifyArchiveRoundoffRecovery(JObject original,JObject proof,string canonicalPlan) {
   try {
    var plan=Parse(canonicalPlan);var steps=(JArray)plan["steps"];
    if(proof.Value<string>("schema")!="foundation-archive-roundoff-recovery-v1"||proof.Value<string>("original_result_sha256")!=Hash(original)||proof.Value<string>("plan_sha256")!=Hash(plan))return false;
    if(original.Value<string>("outcome")!="committed_readback_failed"||original.Value<string>("effects")!="committed"||original.Value<bool?>("commit_invoked")!=true||original.Value<bool?>("commit_returned")!=true||original["error"]?.Type!=JTokenType.Null)return false;
    if(!steps.Any(s=>s.Value<string>("op")=="archive")||steps.Any(s=>s.Value<string>("op") is not ("archive" or "copy")))return false;
    var normalized=(JObject)original.DeepClone();normalized["outcome"]="committed_verified";
    if(!VerifyReceipt(normalized,canonicalPlan))return false;
    foreach(JObject step in steps.Where(s=>s.Value<string>("op")=="archive"))foreach(JObject target in (JArray)step["targets"]) {
     string h=target.Value<string>("handle");var before=(JObject)original["before"][h].DeepClone();var after=(JObject)original["after"][h].DeepClone();before.Remove("layer");after.Remove("layer");if(!RecoveryExactNumeric(before,after))return false;
    }
    if(proof["native_readback"] is not JObject fresh||!VerifyReceipt(fresh,canonicalPlan))return false;
    foreach(string key in new[]{"after","model_handles_after","all_layers_after","symbols_after","layouts_after"})if(!RecoveryExactNumeric(original[key],fresh[key]))return false;
    foreach(string key in new[]{"before","objects","created_handles","model_handles_before","all_layers_before","symbols_before","layouts_before","plan_sha256"})if(!JToken.DeepEquals(original[key],fresh[key]))return false;
    return true;
   }catch{return false;}
  }
 }
}
