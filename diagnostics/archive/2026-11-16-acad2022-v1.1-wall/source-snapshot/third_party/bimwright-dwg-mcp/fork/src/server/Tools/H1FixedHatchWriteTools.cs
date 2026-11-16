using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bimwright.Dwg.Server.Execution;
using Bimwright.Dwg.Shared.Execution;
using ModelContextProtocol.Server;
namespace Bimwright.Dwg.Server.Tools
{
 [McpServerToolType] public static class H1FixedHatchWriteTools
 {
  [McpServerTool(Name=H1FixedHatchWriteContract.McpToolName,ReadOnly=false,Idempotent=false)]
  public static Task<string> RepairFixedHatch()=>BoundPluginExecutionGateway.Shared.SubmitH1WriteAsync(ToolInvocationScope.Current);
  public static IReadOnlyList<McpServerTool> CreateToolInstances()=>typeof(H1FixedHatchWriteTools).GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly)
   .Where(method=>method.GetCustomAttribute<McpServerToolAttribute>()!=null).Select(method=>(McpServerTool)new CommonArgumentMcpServerTool(McpServerTool.Create(method,(object)null,new McpServerToolCreateOptions()),true,true)).ToArray();
 }
}
