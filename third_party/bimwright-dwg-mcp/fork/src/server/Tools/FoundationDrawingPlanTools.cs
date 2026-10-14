using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Bimwright.Dwg.Server.Execution;
using Bimwright.Dwg.Shared.Execution;
using ModelContextProtocol.Server;
namespace Bimwright.Dwg.Server.Tools
{
 [McpServerToolType] public static class FoundationDrawingPlanTools
 {
  [McpServerTool(Name=FoundationDrawingPlan.McpToolName,ReadOnly=false,Idempotent=false),Description("Apply one bounded metre/model-space drawing plan to the explicitly configured, bound working DWG. Supports layers, line/circle/polyline, associative new-boundary hatch, text/mtext, ordinary LEADER/yellow-dot callout, guarded text update/full-group move/archive, or separate whole-document save with specified-object disk verification. Existing updates require fresh native entity digests and complete associated groups. Supply a stable request_id. Submit once; query the original job after an uncertain reply. No code, arbitrary CAD commands, layout edits or cross-drawing copies.")]
  public static Task<string> ApplyPlan([Description("JSON foundation-drawing-plan-v1 with source_role, units=metres and 1..16 steps.")]string plan_json)=>BoundPluginExecutionGateway.Shared.SubmitFoundationPlanAsync(ToolInvocationScope.Current);
  public static IReadOnlyList<McpServerTool> CreateToolInstances()=>typeof(FoundationDrawingPlanTools).GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly)
   .Where(m=>m.GetCustomAttribute<McpServerToolAttribute>()!=null).Select(m=>(McpServerTool)new CommonArgumentMcpServerTool(McpServerTool.Create(m,(object)null,new McpServerToolCreateOptions()),true,true)).ToArray();
 }
}
