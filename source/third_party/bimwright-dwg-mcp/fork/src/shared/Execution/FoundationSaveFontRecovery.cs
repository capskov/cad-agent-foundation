using System;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution {
 public static partial class FoundationDrawingPlan {
  // Read-only re-verification of one already-committed save. Never a new save.
  public static bool VerifySaveFontRecovery(JObject original,JObject proof,string canonicalPlan) {
   try {
    var plan=Parse(canonicalPlan);var steps=(JArray)plan["steps"];
    if(steps.Count!=1||steps[0].Value<string>("op")!="save"
      ||proof.Value<string>("schema")!="foundation-save-font-recovery-v1"
      ||proof.Value<string>("original_result_sha256")!=Hash(original)
      ||proof.Value<string>("plan_sha256")!=Hash(plan))return false;
    if(original.Value<string>("outcome")!="committed_readback_failed"||original.Value<string>("effects")!="committed"
      ||original.Value<bool?>("commit_invoked")!=true||original.Value<bool?>("commit_returned")!=true
      ||original["error"]?.Type!=JTokenType.Null)return false;
    var normalized=(JObject)original.DeepClone();normalized["outcome"]="committed_verified";
    if(!VerifyReceipt(normalized,canonicalPlan))return false;
    if(proof["native_readback"] is not JObject fresh||!VerifyReceipt(fresh,canonicalPlan))return false;
    // The fresh native reader must independently observe the exact saved bytes,
    // selected entities and all protected symbol/layer/layout/model facts.
    foreach(string key in new[]{"before","after","objects","created_handles","model_handles_before","model_handles_after",
      "all_layers_before","all_layers_after","symbols_before","symbols_after","layouts_before","layouts_after",
      "saved_file_sha256","dbmod_after_save","plan_sha256"})
      if(!JToken.DeepEquals(original[key],fresh[key]))return false;
    // Also bind the current live drawing to its pre-save geometry; font filename
    // canonicalization is the sole exception and uses the same save verifier.
    if(proof["live_readback"] is not JObject live||!VerifyReceipt(live,canonicalPlan))return false;
    foreach(string key in new[]{"before","objects","created_handles","model_handles_before","all_layers_before","symbols_before","layouts_before","plan_sha256"})
      if(!JToken.DeepEquals(original[key],live[key]))return false;
    if(!JToken.DeepEquals(live["after"],original["before"])
      ||!JToken.DeepEquals(live["all_layers_after"],original["all_layers_before"])
      ||!JToken.DeepEquals(live["model_handles_after"],original["model_handles_before"])
      ||!JToken.DeepEquals(live["layouts_after"],original["layouts_before"])
      ||!JToken.DeepEquals(live["saved_file_sha256"],original["saved_file_sha256"]))return false;
    return true;
   }catch{return false;}
  }
 }
}
