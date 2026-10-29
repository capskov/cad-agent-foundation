using System;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Server.Execution
{
 public sealed class FoundationWriteScope
 {
  internal D1WritePilotScope ExactScope {get;}
  public FoundationWriteScope(string path,string journalScope,string journalDigest)=>ExactScope=new D1WritePilotScope(path,journalScope,journalDigest);
  public bool Matches(BoundPluginContext bound)=>ExactScope.Matches(bound);
 }
 public sealed partial class BoundPluginExecutionGateway
 {
  public Task<string> SubmitFoundationViewAsync(ToolInvocationEnvelope invocation,string json)
  {
   try {var p=FoundationViewRequest.Parse(json);return SubmitNamedGovernedReadAsync(invocation,FoundationViewRequest.Mcp,p,p);}
   catch(Exception ex){return Task.FromResult(Envelope(Guid.NewGuid().ToString("N"),true,new {status="rejected_before_submission",pending=false,effects="none",registered=false,request_id=invocation?.RequestId,context_id=invocation?.ContextId,error=ex.Message},null));}
  }
  private bool IsFoundationWriteProfile=>_foundationWriteScope!=null;
  public Task<string> SubmitFoundationPlanAsync(ToolInvocationEnvelope invocation)
  {
   if(!IsFoundationWriteProfile||invocation==null||invocation.BusinessArguments?.Count!=1||!invocation.BusinessArguments.TryGetValue("plan_json",out JsonElement input)||input.ValueKind!=JsonValueKind.String)
    return Task.FromResult(Envelope(Guid.NewGuid().ToString("N"),false,null,"The first-release drawing plan is disabled or invalid."));
   try {string canonical=FoundationDrawingPlan.Canonical(FoundationDrawingPlan.Parse(input.GetString()));return SubmitFixedWriteAsync(invocation,canonical);}
   catch(Exception ex){return Task.FromResult(Envelope(Guid.NewGuid().ToString("N"),true,new {status="rejected_before_submission",pending=false,effects="none",registered=false,request_id=invocation?.RequestId,context_id=invocation?.ContextId,error=ex.Message},null));}
  }
  private string OwnedWriteParameters(string requestId,BoundPluginContext bound)
  {
   if(!IsFoundationWriteProfile)return ActiveWriteParameters;
   if(!_contexts.TryGetRequestOwnership(requestId,bound.ContextId,out BoundPluginContext ownerBound,out BoundPluginRequestOwnership owner)||!ReferenceEquals(bound,ownerBound)||!FoundationDrawingPlan.IsCanonicalValid(owner.CanonicalParametersJson))throw new InvalidOperationException("Immutable drawing-plan ownership is unavailable.");
   return owner.CanonicalParametersJson;
  }
  private static JObject CreateFoundationOutcomeSummary(JObject job,LedgerJob ledger,bool finalized)
  {
   JObject result=CreateH1OutcomeSummary(job,ledger,finalized);result["schema"]="bimwright-foundation-outcome-v1";
   result["existing_handles"]=JValue.CreateNull();
   if(job?.Value<string>("effects")=="committed")try
   {var receipt=JObject.Parse(job.Value<string>("result_json")??"{}");if(receipt.Value<string>("schema")=="foundation-plan-receipt-v1")result["existing_handles"]=new JObject{["created_handles"]=receipt["created_handles"]?.DeepClone(),["objects"]=receipt["objects"]?.DeepClone()};}
   catch{}
   return result;
  }
 }
}
