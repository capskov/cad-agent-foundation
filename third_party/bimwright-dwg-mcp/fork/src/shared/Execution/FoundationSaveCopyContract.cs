using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution {
 public static partial class FoundationDrawingPlan {
  static bool ValidateSaveCopy(JObject step,ref int count){
   if(step.Value<string>("op")!="save_copy")return false;
   Keys(step,"id","op","file_name","expected");
   if(!Regex.IsMatch(Text(step,"file_name",100),"^[A-Za-z0-9][A-Za-z0-9_-]{0,79}\\.dwg\\z"))throw new ArgumentException("simple ASCII DWG basename required");
   if(step["expected"] is not JArray targets||targets.Count<1||targets.Count>64)throw new ArgumentException("1..64 explicit copy readback entities required");
   foreach(JObject target in targets){Keys(target,"handle","expected_sha256");Expected(target);}
   if(targets.Cast<JObject>().Select(t=>t.Value<string>("handle")).Distinct().Count()!=targets.Count)throw new ArgumentException("duplicate copy readback entity");
   count+=targets.Count;return true;
  }
  static bool VerifySaveCopy(JObject step,JObject receipt){
   var artifact=receipt["output_file"] as JObject;var map=receipt["objects"]?[step.Value<string>("id")] as JObject;
   if(artifact==null||map==null||artifact.Value<string>("file_name")!=step.Value<string>("file_name")||!Regex.IsMatch(artifact.Value<string>("sha256")??"","^[A-F0-9]{64}$")||artifact.Value<long?>("bytes")<=5||artifact.Value<bool?>("published_without_overwrite")!=true||receipt.Value<string>("effects_scope")!="new_dwg_copy_only"||receipt.Value<bool?>("copy_requires_new_explicit_binding")!=true)return false;
   foreach(string field in new[]{"source_dbmod_before","source_dbmod_after","source_insunits","copy_insunits"})if(receipt[field]?.Type!=JTokenType.Integer)return false;
   if(string.IsNullOrEmpty(receipt.Value<string>("source_name_before"))||receipt.Value<string>("source_name_before")!=receipt.Value<string>("source_name_after")||receipt.Value<int>("source_dbmod_before")!=receipt.Value<int>("source_dbmod_after")||!Regex.IsMatch(receipt.Value<string>("source_file_sha256_before")??"","^[A-F0-9]{64}$")||receipt.Value<string>("source_file_sha256_before")!=receipt.Value<string>("source_file_sha256_after"))return false;
   if(receipt["copy_disk_entities"] is not JObject disk||receipt["copy_disk_layers"] is not JObject||receipt["copy_disk_layouts"] is not JObject||receipt["copy_disk_model_handles"] is not JArray||receipt["copy_disk_symbols"] is not JObject)return false;
   if(!JToken.DeepEquals(receipt["before"],receipt["after"])||!Equivalent(receipt["all_layers_before"],receipt["copy_disk_layers"])||!Equivalent(receipt["layouts_before"],receipt["copy_disk_layouts"])||!JToken.DeepEquals(receipt["model_handles_before"],receipt["copy_disk_model_handles"])||receipt.Value<int?>("source_insunits")!=receipt.Value<int?>("copy_insunits"))return false;
   if(!VerifySymbols(new JObject{["symbols_before"]=receipt["symbols_before"].DeepClone(),["symbols_after"]=receipt["copy_disk_symbols"].DeepClone()},new JArray(new JObject{["op"]="save"})))return false;
   foreach(JObject target in (JArray)step["expected"]){string h=target.Value<string>("handle");if(map.Value<string>(h)!=h||Hash(receipt["before"]?[h])!=target.Value<string>("expected_sha256")||!Equivalent(receipt["before"]?[h],disk[h]))return false;}
   return true;
  }
 }
}
