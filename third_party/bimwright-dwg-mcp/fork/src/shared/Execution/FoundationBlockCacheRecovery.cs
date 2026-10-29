using System;using System.Linq;using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution {public static partial class FoundationDrawingPlan {
 public static bool VerifyBlockCacheRecovery(JObject original,JObject proof,string canonicalPlan){try{
 var plan=Parse(canonicalPlan);var steps=(JArray)plan["steps"];
 if(steps.Count!=2||steps[0].Value<string>("op")!="set_properties"||steps[1].Value<string>("op")!="set_layer"||((JArray)steps[0]["targets"]).Count!=1||proof.Value<string>("schema")!="foundation-block-cache-recovery-v1"||proof.Value<string>("original_result_sha256")!=Hash(original)||proof.Value<string>("plan_sha256")!=Hash(plan))return false;
 string h=steps[0]["targets"][0].Value<string>("handle");if(original["before"]?[h]?.Value<string>("type")!="BlockReference"||proof.Value<string>("source_handle")!=h||proof["disk_geometry"] is not JArray disk||disk.Count==0||disk.Count>4096||!JToken.DeepEquals(disk,proof["live_geometry"]))return false;
 if(original.Value<string>("outcome")!="committed_readback_failed"||original.Value<string>("effects")!="committed"||original.Value<bool?>("commit_invoked")!=true||original.Value<bool?>("commit_returned")!=true||original["error"]?.Type!=JTokenType.Null)return false;
 var normalized=(JObject)original.DeepClone();normalized["outcome"]="committed_verified";
 if(!VerifyReceipt(normalized,canonicalPlan)||proof["live_readback"] is not JObject live||!VerifyReceipt(live,canonicalPlan))return false;
 foreach(string key in new[]{"before","after","objects","created_handles","model_handles_before","model_handles_after","all_layers_before","all_layers_after","symbols_before","symbols_after","layouts_before","layouts_after","plan_sha256"}){if(key=="after"){var a=(JObject)original[key].DeepClone();var b=(JObject)live[key].DeepClone();((JObject)a[h]).Remove("extents");((JObject)b[h]).Remove("extents");if(!JToken.DeepEquals(a,b))return false;}else if(!JToken.DeepEquals(original[key],live[key]))return false;}
 return true;}catch{return false;}}
}}