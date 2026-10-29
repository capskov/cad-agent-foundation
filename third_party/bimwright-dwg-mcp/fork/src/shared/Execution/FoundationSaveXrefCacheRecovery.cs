using System;using System.Linq;using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution {public static partial class FoundationDrawingPlan {
 // Only disappearance of qualified dependent cache rows on a save. Local rows,
 // existing dependent values, xref definitions, handles and layouts remain exact.
 public static JObject NormalizeSaveXrefCache(JObject receipt,JObject footprint=null,JObject overall=null){
 var n=(JObject)receipt.DeepClone();var f=footprint??receipt;
 if(f["symbols_before"]?["xrefs"] is not JObject xr||!JToken.DeepEquals(xr,f["symbols_after"]?["xrefs"]))return n;
 bool Qualified(string name)=>xr.Properties().Any(p=>name.StartsWith(p.Name+"|",StringComparison.OrdinalIgnoreCase));
 void RemoveLost(string table){var a=table=="layers"?f["all_layers_before"] as JObject:f["symbols_before"]?[table] as JObject;var b=table=="layers"?f["all_layers_after"] as JObject:f["symbols_after"]?[table] as JObject;if(a==null||b==null)return;
 foreach(var row in a.Properties().Where(p=>b[p.Name]==null&&Qualified(p.Name)))foreach(var side in new[]{"before","after"}){var o=table=="layers"?n["all_layers_"+side] as JObject:n["symbols_"+side]?[table] as JObject;if(o?[row.Name]!=null&&JToken.DeepEquals(o[row.Name],row.Value))o.Remove(row.Name);}}
 foreach(string t in new[]{"layers","text_styles","dimension_styles","block_definitions"})RemoveLost(t);
 if(overall!=null)foreach(var role in overall.Properties())foreach(string side in new[]{"before","after"}){var rows=n["symbols_"+side]?["paper_viewports"] as JObject;var e=rows?[role.Name];if(e!=null&&e.Value<string>("type")=="Viewport"&&e.Value<string>("layout_name")==role.Value.Value<string>())rows.Remove(role.Name);}return n;
 }
 public static bool VerifySaveXrefCacheRecovery(JObject original,JObject proof,string canonical){try{
 var plan=Parse(canonical);var steps=(JArray)plan["steps"];
 if(steps.Count!=1||steps[0].Value<string>("op")!="save"||proof.Value<string>("schema")!="foundation-save-xref-cache-recovery-v1"||proof.Value<string>("original_result_sha256")!=Hash(original)||proof.Value<string>("plan_sha256")!=Hash(plan))return false;
 if(original.Value<string>("outcome")!="committed_readback_failed"||original.Value<string>("effects")!="committed"||original.Value<bool?>("commit_invoked")!=true||original.Value<bool?>("commit_returned")!=true||original["error"]?.Type!=JTokenType.Null)return false;
 var overall=proof["overall_paper_viewports"] as JObject;var n=NormalizeSaveXrefCache(original,null,overall);if(JToken.DeepEquals(n,original))return false;n["outcome"]="committed_verified";if(!VerifyReceipt(n,canonical))return false;
 foreach(string side in new[]{"native_readback","live_readback"}){if(proof[side] is not JObject raw)return false;var fresh=NormalizeSaveXrefCache(raw,original,overall);if(!VerifyReceipt(fresh,canonical))return false;
 foreach(string key in new[]{"before","after","objects","created_handles","model_handles_before","model_handles_after","all_layers_before","all_layers_after","symbols_before","symbols_after","layouts_before","layouts_after","saved_file_sha256","dbmod_after_save","plan_sha256"})if(!JToken.DeepEquals(n[key],fresh[key]))return false;}
 return true;}catch{return false;}}
}}
