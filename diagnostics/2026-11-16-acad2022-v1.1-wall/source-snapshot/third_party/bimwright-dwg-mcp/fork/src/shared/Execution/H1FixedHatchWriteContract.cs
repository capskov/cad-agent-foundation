using System;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution
{
 public static class H1FixedHatchWriteContract
 {
  public const string McpToolName = "dwg_h1_repair_fixed";
  public const string PluginToolName = "h1_fixed_hatch_repair";
  public const string CanonicalParametersJson = "{}";
  public const string PlanId = H1FixedHatchRepairReadbackContract.PlanId;
  public const string ReadbackValidatorVersion = "h1-fixed-solid-readback-v1";
  public static readonly string[] SourceHandles = { "7A", "7B" };
  private static bool True(JObject value,string field) => value[field]?.Type==JTokenType.Boolean && value.Value<bool>(field);
  public static bool MatchesCompleteScene(JObject before,JObject after)
  {
   try
   {
    return H1FixedHatchRepairReadbackContract.Matches(before,after)
     && JToken.DeepEquals(before["model_handles"],new JArray("78","79","7A","7B"))
     && JToken.DeepEquals(before["model_handles"],after["model_handles"])
     && before["unrelated_entities"] is JObject unrelated
     && unrelated.Properties().Select(p=>p.Name).OrderBy(p=>p,StringComparer.Ordinal).SequenceEqual(new[]{"78","79"})
     && unrelated["78"] is JObject && unrelated["79"] is JObject
     && JToken.DeepEquals(unrelated,after["unrelated_entities"]);
   }
   catch{return false;}
  }
  public static bool VerifyReceipt(JObject result)
  {
   try
   {
    return result!=null && result["schema_version"]?.Type==JTokenType.Integer && result.Value<int>("schema_version")==1
     && result.Value<string>("plan_id")==PlanId && result.Value<string>("boundary_handle")=="7A" && result.Value<string>("hatch_handle")=="7B"
     && result.Value<string>("outcome")=="committed_verified" && result.Value<string>("effects")=="committed"
     && True(result,"commit_invoked") && True(result,"commit_returned") && True(result,"write_opened") && True(result,"object_undo_recording_restored")
     && result.Value<string>("verification")=="passed" && result["error_code"]?.Type==JTokenType.Null
     && MatchesCompleteScene(result["before"] as JObject,result["after"] as JObject);
   }
   catch{return false;}
  }
  public static bool VerifyReadback(string resultJson,string readbackJson)
  {
   try{var result=JObject.Parse(resultJson);var readback=JObject.Parse(readbackJson);return JToken.DeepEquals(result,readback)&&VerifyReceipt(readback);}
   catch{return false;}
  }
 }
}
