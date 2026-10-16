// Native .NET page setup / single PDF output; never sends a physical printer job.
using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using PlotType=Autodesk.AutoCAD.DatabaseServices.PlotType;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Plugin
{
 internal static partial class FoundationPlanWriteHandler
 {
  static JObject LayoutFacts(Database db,Transaction tx)
  {
   var result=new JObject();var dictionary=(DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);
   foreach(DBDictionaryEntry entry in dictionary)
   {
    if(result.Count>=64)throw new InvalidOperationException("layout_limit_64");var l=(Layout)tx.GetObject(entry.Value,OpenMode.ForRead);
    result[l.LayoutName]=new JObject{["handle"]=l.Handle.ToString(),["model_type"]=l.ModelType,["device"]=l.PlotConfigurationName,["media"]=l.CanonicalMediaName,["plot_style"]=l.CurrentStyleSheet,["rotation"]=(int)l.PlotRotation*90,["plot_area"]=l.PlotType==PlotType.Layout?"layout":l.PlotType==PlotType.Extents?"extents":l.PlotType.ToString(),["center"]=l.PlotCentered,["use_standard_scale"]=l.UseStandardScale,["standard_scale"]=l.StdScaleType.ToString(),["scale_numerator"]=l.CustomPrintScale.Numerator,["scale_denominator"]=l.CustomPrintScale.Denominator,["paper_units"]=l.PlotPaperUnits.ToString(),["paper_size"]=new JArray(l.PlotPaperSize.X,l.PlotPaperSize.Y),["origin"]=new JArray(l.PlotOrigin.X,l.PlotOrigin.Y),["margins"]=new JArray(l.PlotPaperMargins.MinPoint.X,l.PlotPaperMargins.MinPoint.Y,l.PlotPaperMargins.MaxPoint.X,l.PlotPaperMargins.MaxPoint.Y)};
   }
   return result;
  }
  static Layout FindLayout(Database db,Transaction tx,string name,OpenMode mode)
  {
   var d=(DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);if(!d.Contains(name))throw new InvalidOperationException("layout_missing:"+name);return (Layout)tx.GetObject(d.GetAt(name),mode);
  }
  static void SetPageSetup(Database db,Transaction tx,JObject s,JObject map)
  {
   string name=s.Value<string>("layout");var l=FindLayout(db,tx,name,OpenMode.ForRead);if(FoundationDrawingPlan.Hash(LayoutFacts(db,tx)[name])!=s.Value<string>("expected_sha256"))throw new InvalidOperationException("layout_changed");
   var v=PlotSettingsValidator.Current;string device=s.Value<string>("device"),style=s.Value<string>("plot_style"),media=s.Value<string>("media");
   if(!v.GetPlotDeviceList().Cast<string>().Contains(device)||!v.GetPlotStyleSheetList().Cast<string>().Contains(style))throw new InvalidOperationException("installed_plot_device_and_style_required");
   using var copy=new PlotSettings(l.ModelType);copy.CopyFrom(l);v.SetPlotConfigurationName(copy,device,null);v.RefreshLists(copy);
   if(!v.GetCanonicalMediaNameList(copy).Cast<string>().Contains(media))throw new InvalidOperationException("media_not_supported_by_selected_device");
   v.SetCanonicalMediaName(copy,media);v.SetPlotPaperUnits(copy,PlotPaperUnit.Millimeters);v.SetPlotRotation(copy,(PlotRotation)(s.Value<int>("rotation")/90));v.SetPlotType(copy,s.Value<string>("plot_area")=="layout"?PlotType.Layout:PlotType.Extents);
   v.SetUseStandardScale(copy,false);v.SetCustomPrintScale(copy,new CustomScale(s.Value<double>("scale_numerator"),s.Value<double>("scale_denominator")));v.SetCurrentStyleSheet(copy,style);
   if(copy.PlotType==PlotType.Extents)v.SetPlotCentered(copy,s.Value<bool>("center"));else v.SetPlotOrigin(copy,Point2d.Origin);
   // All properties are prepared transiently before the one transactional copy.
   l.UpgradeOpen();l.DisableUndoRecording(false);l.CopyFrom(copy);map["layout"]=name;
  }
  static JObject PlotPdf(Document doc,JObject plan,string canonicalPlan)
  {
   var s=(JObject)plan["steps"][0];var empty=new JObject();JObject layers=null,symbols=null,layouts=null;JArray model=null;bool invoked=false,published=false;string output=null,temp=null,error=null;JObject artifact=null;
   string outcome="rejected_before_write";
   using(doc.LockDocument())try
   {
    int units=Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("INSUNITS"));if(units!=0&&units!=6)throw new InvalidOperationException("file_output_requires_metres_or_explicit_unitless_metres");
    if(doc.IsReadOnly||PlotFactory.ProcessPlotState!=ProcessPlotState.NotPlotting)throw new InvalidOperationException("plot_requires_owned_writable_idle_document");
    using var tx=doc.Database.TransactionManager.StartTransaction();var db=doc.Database;layers=LayerFacts(db,tx);symbols=SymbolFacts(db,tx);layouts=LayoutFacts(db,tx);model=Handles(db,tx);
    string name=s.Value<string>("layout");var layout=FindLayout(db,tx,name,OpenMode.ForRead);
    if(FoundationDrawingPlan.Hash(layouts[name])!=s.Value<string>("expected_sha256"))throw new InvalidOperationException("layout_changed");
    if(!layout.PlotConfigurationName.EndsWith(".pc3",StringComparison.OrdinalIgnoreCase)||layout.PlotConfigurationName.IndexOf("PDF",StringComparison.OrdinalIgnoreCase)<0)throw new InvalidOperationException("PDF_PC3_page_setup_required_no_physical_printer_submission");
    if(LayoutManager.Current.CurrentLayout!=name)throw new InvalidOperationException("plot_requires_explicit_current_layout_no_silent_switch");
    string parent=Path.GetDirectoryName(doc.Name);if(string.IsNullOrEmpty(parent)||!File.Exists(doc.Name))throw new InvalidOperationException("saved_owned_drawing_required");
    string folder=Path.Combine(parent,"outputs");
    for(var d=new DirectoryInfo(folder);d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("output_reparse_path_refused");
    output=Path.Combine(folder,s.Value<string>("file_name"));if(File.Exists(output))throw new InvalidOperationException("output_exists_no_overwrite");temp=Path.Combine(folder,"pending-"+Guid.NewGuid().ToString("N")+".pdf");
    using var settings=new PlotSettings(layout.ModelType);settings.CopyFrom(layout);
    using var info=new PlotInfo{Layout=layout.ObjectId,OverrideSettings=settings};using var validator=new PlotInfoValidator{MediaMatchingPolicy=MatchingPolicy.MatchEnabled};validator.Validate(info);if(info.ValidatedSettings.PlotConfigurationName!=settings.PlotConfigurationName||info.ValidatedSettings.CanonicalMediaName!=settings.CanonicalMediaName)throw new InvalidOperationException("plot_validation_changed_device_or_media");
    using(var engine=PlotFactory.CreatePublishEngine())
    {
     invoked=true;Directory.CreateDirectory(folder);engine.BeginPlot(null,null);engine.BeginDocument(info,doc.Name,null,1,true,temp);
     using var page=new PlotPageInfo();engine.BeginPage(page,info,true,null);engine.BeginGenerateGraphics(null);engine.EndGenerateGraphics(null);engine.EndPage(null);engine.EndDocument(null);engine.EndPlot(null);
    }
    if(!File.Exists(temp)||new FileInfo(temp).Length<6)throw new InvalidOperationException("plot_returned_without_PDF_file");
    using(var file=File.OpenRead(temp)){var header=new byte[5];if(file.Read(header,0,5)!=5||System.Text.Encoding.ASCII.GetString(header)!="%PDF-")throw new InvalidOperationException("output_is_not_PDF");}
    string digest;using(var sha=System.Security.Cryptography.SHA256.Create())using(var file=File.OpenRead(temp))digest=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
    File.Move(temp,output,false);published=true;artifact=new JObject{["path"]=output,["file_name"]=s["file_name"],["sha256"]=digest,["bytes"]=new FileInfo(output).Length,["pdf_header_verified"]=true,["published_without_overwrite"]=true};
    if(!JToken.DeepEquals(layouts,LayoutFacts(db,tx))||!JToken.DeepEquals(layers,LayerFacts(db,tx))||!JToken.DeepEquals(symbols,SymbolFacts(db,tx))||!JToken.DeepEquals(model,Handles(db,tx)))throw new InvalidOperationException("plot_drawing_state_changed");
    tx.Abort();outcome="committed_verified";
   }
   catch(Exception ex){error="step="+s.Value<string>("id")+" op=plot_pdf: "+ex.Message;outcome=published?"committed_readback_unknown":invoked?"commit_outcome_unknown":"rejected_before_write";}
   var objects=new JObject{[s.Value<string>("id")]=new JObject{["layout"]=s["layout"]}};
   var result=Build(plan,empty,empty,objects,new JObject(),layers,layers,model,model,Array.Empty<string>(),outcome,invoked,published,error,symbols,symbols);result["layouts_before"]=layouts;result["layouts_after"]=layouts;result["effects_scope"]="output_file_only";result["output_file"]=artifact;result["pending_output_path"]=temp;result["failed_step"]=error==null?JValue.CreateNull():s["id"].DeepClone();
   if(outcome=="committed_verified"&&!FoundationDrawingPlan.VerifyReceipt(result,canonicalPlan)){result["outcome"]="committed_readback_failed";result["error"]="plot_receipt_verification_failed";}
   return result;
  }
 }
}
