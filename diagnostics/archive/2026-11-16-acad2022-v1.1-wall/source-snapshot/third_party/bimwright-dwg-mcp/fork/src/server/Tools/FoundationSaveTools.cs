using System;
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
 [McpServerToolType] public static class FoundationSaveTools
 {
  [McpServerTool(Name=FoundationSaveRequest.Mcp,ReadOnly=false,Idempotent=false),Description("Save the explicitly bound owned drawing to one absolute .dwg path (AutoCAD 2022 persistence route). Does not edit geometry. Registered only when the host enabled plugin-side save; the write finalization guard requires such a completed save before a write's fence may be released. request_json fields: output_path (absolute local .dwg), optional confirm, overwrite_existing, allow_repo_output. Submit once with a stable request_id and query the original request; never resubmit.")]
  public static Task<string> Save(string request_json)=>BoundPluginExecutionGateway.Shared.SubmitFoundationSaveAsync(ToolInvocationScope.Current,request_json);
  public static IReadOnlyList<McpServerTool> CreateToolInstances()=>typeof(FoundationSaveTools).GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly)
   .Where(m=>m.GetCustomAttribute<McpServerToolAttribute>()!=null).Select(m=>(McpServerTool)new CommonArgumentMcpServerTool(McpServerTool.Create(m,(object)null,new McpServerToolCreateOptions()),true,true)).ToArray();
 }
}
