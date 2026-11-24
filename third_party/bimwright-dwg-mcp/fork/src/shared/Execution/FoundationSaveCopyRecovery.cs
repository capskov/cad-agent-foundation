using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution {
 public static partial class FoundationDrawingPlan {
  // Resolves only the diagnosed legacy SaveAs DBMOD 0->1 case. The original
  // result remains immutable; fresh proof is stored as independent readback.
  public static bool VerifySaveCopyRecovery(JObject original,JObject proof,string canonicalPlan) {
   try {
    var plan=Parse(canonicalPlan);var steps=(JArray)plan["steps"];
    if(steps.Count!=1||steps[0].Value<string>("op")!="save_copy"
      ||proof.Value<string>("schema")!="foundation-save-copy-recovery-v1"
      ||proof.Value<string>("original_result_sha256")!=Hash(original)
      ||proof.Value<string>("plan_sha256")!=Hash(plan)
      ||proof.Value<bool?>("save_replayed")!=false
      ||proof.Value<string>("warning")!="legacy_saveas_dirty_flag_only")return false;
    if(original.Value<string>("outcome")!="commit_outcome_unknown"||original.Value<string>("effects")!="unknown"
      ||original.Value<bool?>("commit_invoked")!=true||original.Value<bool?>("commit_returned")!=false
      ||original.Value<string>("error")!="save_copy: copy_disk_or_identity_readback_mismatch"
      ||original.Value<int?>("source_dbmod_before")!=0||original.Value<int?>("source_dbmod_after")!=1
      ||original["output_file"]?.Value<bool?>("published_without_overwrite")!=false)return false;
    if(proof["native_readback"] is not JObject fresh)return false;
    var normalized=(JObject)original.DeepClone();normalized["outcome"]="committed_verified";normalized["effects"]="committed";
    normalized["commit_returned"]=true;normalized["error"]=JValue.CreateNull();normalized["source_dbmod_before"]=1;
    normalized["output_file"]["published_without_overwrite"]=true;
    if(!Equivalent(normalized,fresh)||!VerifyReceipt(fresh,canonicalPlan))return false;
    string expected=Path.Combine(Path.GetDirectoryName(original.Value<string>("source_name_before")),steps[0].Value<string>("file_name"));
    if(!string.Equals(Path.GetFullPath(expected),Path.GetFullPath(fresh["output_file"].Value<string>("path")),StringComparison.OrdinalIgnoreCase)
      ||proof.Value<string>("published_sha256")!=fresh["output_file"].Value<string>("sha256")
      ||proof.Value<long?>("published_bytes")!=fresh["output_file"].Value<long?>("bytes")
      ||proof.Value<string>("source_sha256")!=original.Value<string>("source_file_sha256_before")
      ||proof.Value<int?>("live_dbmod")!=1)return false;
    if(proof["live_snapshot"] is not JObject live||proof["source_disk_snapshot"] is not JObject source
      ||proof["copy_disk_snapshot"] is not JObject copy||!Equivalent(live,source)||!Equivalent(live,copy))return false;
    foreach(string key in new[]{"entities","layers","symbols","layouts"})if(live[key] is not JObject)return false;
    if(live["model_handles"] is not JArray handles||live.Value<int?>("insunits")!=original.Value<int?>("source_insunits")
      ||!JToken.DeepEquals(handles,original["model_handles_before"]))return false;
    var entities=(JObject)live["entities"];
    if(entities.Count!=handles.Count||handles.Any(h=>entities[h.Value<string>()] is not JObject))return false;
    foreach(var p in ((JObject)original["before"]).Properties())if(!Equivalent(p.Value,entities[p.Name]))return false;
    foreach(var pair in new[]{("layers","all_layers_before"),("symbols","symbols_before"),("layouts","layouts_before")})
      if(!Equivalent(live[pair.Item1],original[pair.Item2]))return false;
    return true;
   }catch{return false;}
  }
 }
}
