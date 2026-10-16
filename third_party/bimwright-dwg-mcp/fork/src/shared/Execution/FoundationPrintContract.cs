// Native page setup and file plotting use explicit layout digests and a single-step plan.
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution
{
 public static partial class FoundationDrawingPlan
 {
  public static string[] PrintOperations=>new[]{"page_setup","plot_pdf"};
  static bool ValidatePrint(JObject s)
  {
   string op=s.Value<string>("op");if(!PrintOperations.Contains(op))return false;
   if(op=="page_setup")
   {
    Keys(s,"id","op","layout","expected_sha256","device","media","plot_style","rotation","plot_area","scale_numerator","scale_denominator","center");
    foreach(string k in new[]{"layout","device","media","plot_style"})Text(s,k,255);Digest(s,"expected_sha256");
    if(s["rotation"]?.Type!=JTokenType.Integer||!new[]{0,90,180,270}.Contains(s.Value<int>("rotation")))throw new ArgumentException("rotation must be 0,90,180,270 degrees");
    if(!new[]{"layout","extents"}.Contains(Text(s,"plot_area",32)))throw new ArgumentException("plot_area must be layout or extents");Positive(s,"scale_numerator");Positive(s,"scale_denominator");Bool(s,"center");
    if(s.Value<string>("plot_area")=="layout"&&s.Value<bool>("center"))throw new ArgumentException("centering is not applicable to layout plots");
   }
   else
   {
    Keys(s,"id","op","layout","expected_sha256","file_name");Text(s,"layout",255);Digest(s,"expected_sha256");
    if(!Regex.IsMatch(Text(s,"file_name",100),"^[A-Za-z0-9][A-Za-z0-9_-]{0,79}\\.pdf\\z"))throw new ArgumentException("file_name must be a simple ASCII PDF basename");
   }
   return true;
  }
  public static bool VerifyLayoutChanges(JObject receipt,JArray steps)
  {
   var setup=steps.Cast<JObject>().Where(s=>s.Value<string>("op")=="page_setup").ToArray();
   if(receipt["layouts_before"] is not JObject old||receipt["layouts_after"] is not JObject next)return setup.Length==0&&!steps.Cast<JObject>().Any(s=>s.Value<string>("op")=="plot_pdf");
   if(old.Count!=next.Count)return false;
   foreach(var row in old.Properties())
   {
    if(next[row.Name] is not JObject actual)return false;var step=setup.SingleOrDefault(s=>s.Value<string>("layout")==row.Name);
    if(step==null){if(!Equivalent(row.Value,actual))return false;continue;}
    if(Hash(row.Value)!=step.Value<string>("expected_sha256")||actual.Value<string>("device")!=step.Value<string>("device")||actual.Value<string>("media")!=step.Value<string>("media")||actual.Value<string>("plot_style")!=step.Value<string>("plot_style")||actual.Value<int?>("rotation")!=step.Value<int>("rotation")||actual.Value<string>("plot_area")!=step.Value<string>("plot_area")||actual.Value<bool?>("center")!=step.Value<bool>("center")||actual.Value<bool?>("use_standard_scale")!=false||!Near(actual["scale_numerator"],Number(step["scale_numerator"]),1e-10)||!Near(actual["scale_denominator"],Number(step["scale_denominator"]),1e-10)||actual.Value<string>("paper_units")!="Millimeters")return false;
    var a=(JObject)row.Value.DeepClone();var b=(JObject)actual.DeepClone();foreach(string f in new[]{"device","media","plot_style","rotation","plot_area","center","scale_numerator","scale_denominator","use_standard_scale","paper_units","paper_size","origin","margins","standard_scale"}){a.Remove(f);b.Remove(f);}if(!Equivalent(a,b))return false;
   }
   return setup.All(s=>old[s.Value<string>("layout")]!=null);
  }
  static bool VerifyPrint(JObject step,JObject receipt,out bool handled)
  {
   string op=step.Value<string>("op");handled=PrintOperations.Contains(op);if(!handled)return true;
   var map=receipt["objects"]?[step.Value<string>("id")] as JObject;string name=step.Value<string>("layout");
   if(map?.Value<string>("layout")!=name||receipt["layouts_before"]?[name]==null||Hash(receipt["layouts_before"][name])!=step.Value<string>("expected_sha256"))return false;
   if(op=="page_setup")return true;
   var file=receipt["output_file"] as JObject;
   return file!=null&&file.Value<string>("file_name")==step.Value<string>("file_name")&&Regex.IsMatch(file.Value<string>("sha256")??"","^[A-F0-9]{64}$")&&file.Value<long?>("bytes")>5&&file.Value<bool?>("pdf_header_verified")==true&&file.Value<bool?>("published_without_overwrite")==true&&receipt.Value<string>("effects_scope")=="output_file_only";
  }
 }
}
