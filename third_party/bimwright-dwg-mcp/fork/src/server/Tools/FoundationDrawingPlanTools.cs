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
  [McpServerTool(Name=FoundationDrawingPlan.McpToolName,ReadOnly=false,Idempotent=false),Description("Apply one bounded explicit-coordinate/model-space drawing plan to the explicitly configured, bound working DWG. Supports layer creation/state management, installed-font text styles and dimension styles, new line/circle/polyline, associative hatch, text/mtext, styled ordinary LEADER/yellow-dot callout, native linear/aligned/radial/diameter dimensions, guarded text/closed-boundary/callout updates, full-group move/rotate/scale/copy/archive, or separate whole-document save with specified-object disk verification. Catalog and fresh layer digests are in bind document_info.foundation_catalog; entity digests come from explicit reads. text_style creates/reuses matching styles; set_text_style_font explicitly replaces an existing local style with an installed SHX pair using its fresh style SHA256, affecting all references; it preserves metrics and inline font overrides. set_text_metrics scales standalone planar model-space DBText height/width around its existing anchor (excluding fit/aligned text, attributes and reactors). Agent visual review determines suitable fonts and factors. Existing updates require complete associated groups. Supply a stable request_id. Submit once; query the original job after an uncertain reply. Additional bounded operations are enumerated in that actual catalog: point/arc/ellipse/rectangle, standalone primitive/open-polyline/property/text/dimension edits, line/circle offset and mirror-copy, angular dimensions, simple named blocks/attributes, bounded native tables/cells, page setup and current-layout PDF, disk-verified DXF. Page/file operations are separate single-step plans and require their explicit digests/filenames. No code, arbitrary CAD commands, arbitrary layout/xref/proxy edits, physical/batch printer jobs or cross-drawing copies.")]
  public static Task<string> ApplyPlan([Description("JSON foundation-drawing-plan-v1 with source_role, units=raw_drawing_units (recommended; optional expected_insunits consistency check; no coordinate conversion) or legacy units=metres, and 1..16 steps. INSUNITS is auxiliary metadata, not an ordinary raw-operation prerequisite.")]string plan_json)=>BoundPluginExecutionGateway.Shared.SubmitFoundationPlanAsync(ToolInvocationScope.Current);
  [McpServerTool(Name=FoundationViewRequest.Mcp,ReadOnly=false,Idempotent=false),Description("Read or navigate the explicitly bound owned AutoCAD 2026 document view and optionally capture a PNG. No geometry editing or DWG save. request_json action=read|window|center|capture; window uses ordered WCS min_x,min_y,max_x,max_y; center uses x,y,height. Navigation supports single untwisted orthographic top model view only. Optional new file_name PNG is saved under the working drawing outputs directory, with pixel_size 64..4096. Returns exact document/camera/bounds and native regen/readback status; does not claim external image tiles visually settled. Submit once and query original request; no arbitrary commands.")]
  public static Task<string> View(string request_json)=>BoundPluginExecutionGateway.Shared.SubmitFoundationViewAsync(ToolInvocationScope.Current,request_json);
  public static IReadOnlyList<McpServerTool> CreateToolInstances()=>typeof(FoundationDrawingPlanTools).GetMethods(BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly)
   .Where(m=>m.GetCustomAttribute<McpServerToolAttribute>()!=null).Select(m=>(McpServerTool)new CommonArgumentMcpServerTool(McpServerTool.Create(m,(object)null,new McpServerToolCreateOptions()),true,true)).ToArray();
 }
}
