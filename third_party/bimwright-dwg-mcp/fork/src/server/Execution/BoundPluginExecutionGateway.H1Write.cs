using System;
using System.Linq;
using System.Threading.Tasks;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Server.Execution
{
 public sealed class H1WritePilotScope
 {
  internal D1WritePilotScope FixedScope { get; }
  public J10WritePilotPlan J10Plan { get; }
  public H1WritePilotScope(string path,string scopeId,string digest,J10WritePilotPlan j10Plan=null)
  {
   FixedScope=new D1WritePilotScope(path,scopeId,digest);J10Plan=j10Plan;
   if(j10Plan!=null&&(!DrawingRiskKey.TryNormalizePath(j10Plan.DrawingPath,out string planKey)||planKey!=FixedScope.DrawingRiskKey))throw new ArgumentException("J10 plan drawing differs from fixed scope.");
  }
  public bool Matches(BoundPluginContext context)
  {
   if(!FixedScope.Matches(context))return false;
   if(J10Plan==null)return true;
   try{return Guid.TryParse(JObject.Parse(context.CompleteTargetJson).Value<string>("database_fingerprint"),out Guid guid)&&guid.ToString()==J10Plan.DatabaseFingerprint;}catch{return false;}
  }
 }
 public sealed partial class BoundPluginExecutionGateway
 {
  // Sealed fixed presets share the proven journal pipeline; business arguments remain empty.
  private bool IsH1WriteProfile => _h1WritePilotScope != null;
  private bool ActiveWriteScopeMatches(BoundPluginContext bound)=>IsFoundationWriteProfile?_foundationWriteScope.Matches(bound):IsH1WriteProfile?_h1WritePilotScope.Matches(bound):_d1WritePilotScope?.Matches(bound)==true;
  private D1WritePilotScope ActiveFixedWriteScope => _foundationWriteScope?.ExactScope ?? _h1WritePilotScope?.FixedScope ?? _d1WritePilotScope;
  private string ActiveWriteMcpName => IsFoundationWriteProfile ? FoundationDrawingPlan.McpToolName : IsH1WriteProfile ? H1FixedHatchWriteContract.McpToolName : Road08R01D1WriteContract.McpToolName;
  private string ActiveWritePluginName => IsFoundationWriteProfile ? FoundationDrawingPlan.PluginToolName : IsH1WriteProfile ? H1FixedHatchWriteContract.PluginToolName : Road08R01D1WriteContract.PluginToolName;
  private string ActiveWriteParameters => IsH1WriteProfile ? "{}" : Road08R01D1WriteContract.CanonicalParametersJson;
  private string[] ActiveWriteSourceHandles => IsFoundationWriteProfile ? Array.Empty<string>() : _h1WritePilotScope?.J10Plan!=null ? J10FixedHatchWriteContract.SourceHandles : IsH1WriteProfile ? H1FixedHatchWriteContract.SourceHandles : Road08R01D1WriteContract.SourceHandles;
  private string ActiveWriteValidator => IsFoundationWriteProfile ? FoundationDrawingPlan.Validator : _h1WritePilotScope?.J10Plan!=null ? J10FixedHatchWriteContract.ReadbackValidatorVersion : IsH1WriteProfile ? H1FixedHatchWriteContract.ReadbackValidatorVersion : Road08R01D1WriteContract.ReadbackValidatorVersion;
  public Task<string> SubmitH1WriteAsync(ToolInvocationEnvelope invocation) => IsH1WriteProfile
   ? SubmitFixedWriteAsync(invocation) : Task.FromResult(Envelope(Guid.NewGuid().ToString("N"),false,null,"H1 write pilot is disabled"));
  public Task<string> SubmitD1WriteAsync(ToolInvocationEnvelope invocation) => !IsH1WriteProfile && _d1WritePilotScope != null
   ? SubmitFixedWriteAsync(invocation) : Task.FromResult(Envelope(Guid.NewGuid().ToString("N"),false,null,"D1 write pilot is disabled"));
  internal Task<string> GetH1WriteJobAsync(string contextId,string requestId,int waitMs) => IsH1WriteProfile
   ? GetD1WriteJobAsync(contextId,requestId,waitMs) : Task.FromResult(Envelope(Guid.NewGuid().ToString("N"),false,null,"H1 write status requires its configured scope"));
  private bool ReserveFixedWriteOwnership(string requestId,string contextId,string json,string riskKey,out BoundPluginContext bound,out RequestSubmissionLease lease,out string error,bool allowCreate)
   => IsFoundationWriteProfile ? _contexts.TryReserveFoundationWriteRequest(requestId,contextId,json,riskKey,out bound,out lease,out error,allowCreate) : IsH1WriteProfile ? _contexts.TryReserveH1WriteRequest(requestId,contextId,json,riskKey,out bound,out lease,out error,allowCreate)
     : _contexts.TryReserveWriteRequest(requestId,contextId,json,riskKey,out bound,out lease,out error,allowCreate);
  private D1ReadbackResult VerifyActiveWriteReadback(JObject job)
  {
   if(IsFoundationWriteProfile){bool valid=job?.Value<string>("verification")=="passed"&&FoundationDrawingPlan.VerifyReadback(job.Value<string>("result_json"),job.Value<string>("verification_result_json"),job.Value<string>("canonical_parameters_json"));return new D1ReadbackResult(valid,valid?Array.Empty<string>():new[]{"foundation_plan_readback_mismatch"});}
   if(!IsH1WriteProfile)return D1Road08ReadbackVerifier.Verify(job,ActiveWriteParameters);
   bool passed=job?.Value<string>("verification")=="passed" && (_h1WritePilotScope.J10Plan!=null
    ? J10FixedHatchWriteContract.VerifyReadback(job.Value<string>("result_json"),job.Value<string>("verification_result_json"),_h1WritePilotScope.J10Plan)
    : H1FixedHatchWriteContract.VerifyReadback(job.Value<string>("result_json"),job.Value<string>("verification_result_json")));
   return new D1ReadbackResult(passed,passed?Array.Empty<string>():new[]{"h1_complete_scene_readback_mismatch"});
  }
  private static JObject CreateH1OutcomeSummary(JObject job,LedgerJob ledger,bool finalized)
  {
   bool j10=false;try{var receipt=JObject.Parse(job?.Value<string>("result_json")??"{}");j10=receipt.Value<string>("schema")=="j10-native-repair-receipt-v1"&&receipt.Value<string>("boundary_handle")=="17A0D"&&receipt.Value<string>("hatch_handle")=="17A0E";}catch{}
   string effects=job?.Value<string>("effects")??"unknown", phase=job?.Value<string>("phase")??"unknown";
   string pluginVerification=job?.Value<string>("verification")??"unknown", serverVerification=ledger?.Verification.ToString().ToLowerInvariant()??"unknown";
   bool released=ledger?.WriteFenceReleased==true && job?.Value<bool?>("write_fence_released")==true;
   bool complete=finalized&&released&&phase=="completed"&&effects=="committed"&&serverVerification=="passed"&&pluginVerification=="passed";
   string outcome=complete?"committed_verified":effects=="committed"&&(pluginVerification=="failed"||serverVerification=="failed")?"committed_verification_failed"
    :effects=="committed"?"committed_verification_pending":effects=="none"&&phase is "failed" or "cancelled"?"no_effect_terminal":phase is "queued" or "running"?"awaiting_execution":"unknown";
   return new JObject { ["schema"]="bimwright-h1-outcome-v1",["effects"]=effects,["outcome"]=outcome,
    ["awaiting_execution"]=effects=="committed"?new JValue(false):phase is "queued" or "running"?new JValue(true):JValue.CreateNull(),
    ["write_blocked"]=!released||effects=="unknown"||effects=="committed"&&!complete,
    ["existing_handles"]=effects=="committed"?new JObject{["boundary_handle"]=j10?"17A0D":"7A",["hatch_handle"]=j10?"17A0E":"7B"}:JValue.CreateNull(),
    ["plugin_verification"]=pluginVerification,["server_verification"]=serverVerification,
    ["next_action"]=complete?"none":effects=="committed"?"inspect_existing_handles":outcome=="no_effect_terminal"?"review_no_effect_result":"query_original_request"};
  }
 }
}
