using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using Bimwright.Dwg.Plugin.Handlers;
using Bimwright.Dwg.Plugin.View;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Plugin
{
 public sealed class FoundationViewHandler:IAcadCommand
 {
  public string Name=>FoundationViewRequest.Plugin;
  public string Description=>"Bound-document native view navigation/readback/capture; no engineering geometry changes.";
  public CommandSchema Schema=>CommandSchema.Object();
  public CommandResult Execute(Document doc,JToken input)
  {
   bool applied=false;
   try
   {
    var p=FoundationViewRequest.Parse(input.ToString());
    string action=p.Value<string>("action"),output=null;
    // Validate output before changing the view; never overwrite prior evidence.
    if(p["file_name"]!=null){
     string folder=Path.Combine(Path.GetDirectoryName(doc.Name),"outputs");
     for(var d=new DirectoryInfo(folder);d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("output_reparse_path_refused");
     output=Path.Combine(folder,p.Value<string>("file_name"));
     if(File.Exists(output))throw new InvalidOperationException("output_exists_no_overwrite");
    }
    var before=CaptureViewService.ReadContext(doc);
    if(action=="window"||action=="center")
    {
     string reason=ViewReadingContract.UnsupportedReason(before);if(reason!=null)throw new InvalidOperationException(reason);
     double aspect=before["camera"].Value<double>("width")/before["camera"].Value<double>("height");
     double x,y,height;
     if(action=="window"){
      x=p.Value<double>("min_x")/2+p.Value<double>("max_x")/2;y=p.Value<double>("min_y")/2+p.Value<double>("max_y")/2;
      height=Math.Max(p.Value<double>("max_y")-p.Value<double>("min_y"),(p.Value<double>("max_x")-p.Value<double>("min_x"))/aspect)*1.04;
     }else{x=p.Value<double>("x");y=p.Value<double>("y");height=p.Value<double>("height");}
     if(!double.IsFinite(height)||!double.IsFinite(height*aspect)||height<=0)throw new InvalidOperationException("view_size_out_of_range");
     using(var v=doc.Editor.GetCurrentView()){
      v.CenterPoint=new Point2d(x-v.Target.X,y-v.Target.Y);v.Height=height;v.Width=height*aspect;
      doc.Editor.SetCurrentView(v);applied=true;
     }
     doc.Editor.Regen();Autodesk.AutoCAD.ApplicationServices.Core.Application.UpdateScreen();
     var after=CaptureViewService.ReadContext(doc);
     if(!JToken.DeepEquals(before["document"],after["document"])||!JToken.DeepEquals(before["viewport"],after["viewport"]))throw new InvalidOperationException("document_or_viewport_changed_during_navigation");
     var c=after["camera"];double actualX=(double)c["center_dcs"][0]+(double)c["target_wcs"][0],actualY=(double)c["center_dcs"][1]+(double)c["target_wcs"][1];
     if(Math.Abs(actualX-x)>Math.Max(1e-6,height*1e-8)||Math.Abs(actualY-y)>Math.Max(1e-6,height*1e-8)||Math.Abs(c.Value<double>("height")-height)>Math.Max(1e-6,height*1e-8))throw new InvalidOperationException("native_view_readback_mismatch");
    }
    // Regen also precedes capture-only requests; completed API work is reported,
    // not a claim that every external image tile or driver has visually settled.
    if(output!=null){doc.Editor.Regen();Autodesk.AutoCAD.ApplicationServices.Core.Application.UpdateScreen();}
    var context=CaptureViewService.ReadContext(doc);JObject capture=null;
    bool postCaptureRegen=false;
    if(output!=null){
     Directory.CreateDirectory(Path.GetDirectoryName(output));
     capture=CaptureViewService.Capture(doc,output,p.Value<int?>("pixel_size")??1600,false,context["document"].Value<string>("fingerprint"),context);
     // Preview generation can leave the interactive raster display stale even
     // when the PNG is complete. Restore graphics after preview, not only before.
     doc.Editor.Regen();doc.Editor.UpdateScreen();Autodesk.AutoCAD.ApplicationServices.Core.Application.UpdateScreen();
     var refreshed=CaptureViewService.ReadContext(doc);
     CaptureViewContract.EnsureUnchanged(context,refreshed);context=refreshed;postCaptureRegen=true;
    }
    var result=new JObject{["action"]=action,["view_changed"]=applied,["geometry_modified"]=false,["native_regen_completed"]=applied||output!=null,["display_settle_verified"]=false,["post_capture_regen_completed"]=postCaptureRegen,["context"]=context,["capture"]=capture,["captured_after_native_regen"]=output!=null};
    if(ViewReadingContract.UnsupportedReason(context)==null){var c=context["camera"];double x=(double)c["center_dcs"][0]+(double)c["target_wcs"][0],y=(double)c["center_dcs"][1]+(double)c["target_wcs"][1],w=c.Value<double>("width"),h=c.Value<double>("height");result["wcs_xy_bounds"]=new JObject{["min_x"]=x-w/2,["min_y"]=y-h/2,["max_x"]=x+w/2,["max_y"]=y+h/2};}
    return CommandResult.Success(result);
   }
   catch(Exception ex){return CommandResult.Success(new JObject{["ok"]=false,["view_may_have_changed"]=applied,["geometry_modified"]=false,["error"]=ex.Message,["retry_automatically"]=false});}
  }
 }
}
