using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution
{
 /// <summary>
 /// One bounded, explicit save of the bound owned drawing to an absolute .dwg path.
 /// This is the AutoCAD 2022 persistence route: the upstream plugin forbids saving
 /// itself on 2026, so the migrated host exposes saving only as a separate
 /// single-purpose request that never edits geometry.
 /// </summary>
 public static class FoundationSaveRequest
 {
  public const string Mcp="dwg_foundation_save_drawing", Plugin="save_drawing";
  private static readonly string[] Allowed=new[]{"output_path","confirm","overwrite_existing","allow_repo_output"};
  public static JObject Parse(string json)
  {
   if(json==null||json.Length>4096)throw new ArgumentException("save request exceeds 4096 characters");
   var p=JObject.Parse(json,new JsonLoadSettings{DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error});
   if(p["output_path"]==null)throw new ArgumentException("save request requires output_path");
   foreach(var prop in p.Properties())if(!Allowed.Contains(prop.Name))throw new ArgumentException("unexpected save field: "+prop.Name);
   if(p["output_path"].Type!=JTokenType.String)throw new ArgumentException("output_path must be a string");
   string path=p.Value<string>("output_path");
   if(path.Length<4||path.Length>1024||!Regex.IsMatch(path,@"^[A-Za-z]:\\")||!path.EndsWith(".dwg",StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("output_path must be an absolute local .dwg path");
   if(path.Contains(".."))throw new ArgumentException("output_path must not contain parent traversal");
   foreach(string flag in new[]{"confirm","overwrite_existing","allow_repo_output"})
    if(p[flag]!=null&&p[flag].Type!=JTokenType.Boolean)throw new ArgumentException(flag+" must be a boolean");
   return p;
  }
  public static string Canonical(string json)=>FoundationDrawingPlan.Canonical(Parse(json));
  public static bool Valid(string json){try{return Canonical(json)==json;}catch{return false;}}
 }
}
