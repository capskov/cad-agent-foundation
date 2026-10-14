using Autodesk.AutoCAD.ApplicationServices;
using Bimwright.Dwg.Plugin.Execution;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json;
namespace Bimwright.Dwg.Plugin
{
 internal static class H1FixedHatchWriteHandler
 {
  internal static PluginWriteJobResult Execute(Document document,PluginJobSnapshot job)
  {
   if(!App.H1WritePilotEnabled || job==null || !job.IsWrite || job.ToolName!=H1FixedHatchWriteContract.PluginToolName || job.CanonicalParametersJson!=H1FixedHatchWriteContract.CanonicalParametersJson)
    return PluginWriteJobResult.RejectedBeforeTransaction("h1_write_disabled_or_invalid","The fixed H1 write is disabled or its contract is invalid.");
   var plan=App.J10WritePlan;
   var result=plan==null?H1FixedHatchRepairMethod.Execute(document):J10FixedHatchRepairMethod.Execute(document,plan.DrawingPath,plan.DatabaseFingerprint,plan.PairDigest);
   if(plan!=null)result["pilot_plan_digest"]=plan.Digest;
   string json=result.ToString(Formatting.None);
   switch(result.Value<string>("outcome"))
   {
    case "rejected_before_write":return PluginWriteJobResult.RejectedBeforeTransaction("h1_fixed_preflight_blocked","The frozen H1 baseline does not match.");
    case "aborted_verified":return PluginWriteJobResult.RolledBackBeforeCommit("h1_rollback_verified","The complete frozen scene was restored before commit.");
    case "committed_verified":
     if(plan==null?H1FixedHatchWriteContract.VerifyReceipt(result):J10FixedHatchWriteContract.VerifyReceipt(result,plan))return PluginWriteJobResult.Committed(json,PluginVerificationState.Passed,json);
     return PluginWriteJobResult.Committed(json,PluginVerificationState.Failed,json,"h1_readback_failed","The fixed readback did not verify.");
    case "committed_readback_failed":return PluginWriteJobResult.Committed(json,PluginVerificationState.Failed,json,"h1_readback_failed","The fixed readback did not verify.");
    case "committed_readback_unknown":return PluginWriteJobResult.Committed(json,PluginVerificationState.Unknown,null,"h1_readback_unknown","Commit returned; query the original request and inspect the existing pair.");
    default:return PluginWriteJobResult.OutcomeUnknown("h1_write_outcome_unknown","The write outcome is unknown; retain the fence and never replay.");
   }
  }
 }
}
