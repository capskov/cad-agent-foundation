// Read-only native device/page-setup discovery. No plot jobs are submitted.
using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin
{
 internal static partial class FoundationPlanWriteHandler
 {
  static JObject PrintCatalog(Document doc)
  {
   var result=new JObject{["observation"]="bind_time",["complete"]=false,["devices"]=new JArray(),["plot_styles"]=new JArray(),["layouts"]=new JArray(),["errors"]=new JArray(),["job_submission_enabled"]=false};
   var errors=(JArray)result["errors"];
   try
   {
    var validator=PlotSettingsValidator.Current;
    foreach(string device in validator.GetPlotDeviceList()){if(((JArray)result["devices"]).Count>=128){errors.Add("device_limit_128");break;}((JArray)result["devices"]).Add(device);}
    var deviceMedia=new JObject();result["pc3_media"]=deviceMedia;
    foreach(string device in validator.GetPlotDeviceList())
    {
     if(!device.EndsWith(".pc3",StringComparison.OrdinalIgnoreCase))continue;
     if(deviceMedia.Count>=32){result["pc3_media_truncated"]=true;break;}
     try{using var copy=new PlotSettings(true);validator.SetPlotConfigurationName(copy,device,null);validator.RefreshLists(copy);var media=new JArray();foreach(string name in validator.GetCanonicalMediaNameList(copy)){if(media.Count>=512)break;media.Add(name);}deviceMedia[device]=new JObject{["media"]=media,["complete"]=media.Count<512};}
     catch(Exception ex){deviceMedia[device]=new JObject{["complete"]=false,["error"]=ex.Message};}
    }
    result["pc3_media_scope"]="local_pc3_devices_only_up_to_32;physical_device_media_not_queried";
    foreach(string style in validator.GetPlotStyleSheetList()){if(((JArray)result["plot_styles"]).Count>=512){errors.Add("style_limit_512");break;}((JArray)result["plot_styles"]).Add(style);}
    using var tx=doc.Database.TransactionManager.StartTransaction();var layouts=(DBDictionary)tx.GetObject(doc.Database.LayoutDictionaryId,OpenMode.ForRead);
    foreach(DBDictionaryEntry entry in layouts)
    {
     if(((JArray)result["layouts"]).Count>=64){errors.Add("layout_limit_64");break;}
     var layout=(Layout)tx.GetObject(entry.Value,OpenMode.ForRead);
     var facts=new JObject{["name"]=layout.LayoutName,["model_type"]=layout.ModelType,["device"]=layout.PlotConfigurationName,["canonical_media_name"]=layout.CanonicalMediaName,["plot_style"]=layout.CurrentStyleSheet,["plot_type"]=layout.PlotType.ToString(),["paper_units"]=layout.PlotPaperUnits.ToString(),["rotation"]=layout.PlotRotation.ToString(),["plot_centered"]=layout.PlotCentered,["use_standard_scale"]=layout.UseStandardScale,["standard_scale"]=layout.StdScaleType.ToString(),["custom_scale"]=new JArray(layout.CustomPrintScale.Numerator,layout.CustomPrintScale.Denominator),["paper_size"]=new JArray(layout.PlotPaperSize.X,layout.PlotPaperSize.Y)};
     // Copy settings into a transient object: device/media discovery must not write a layout.
     try{using var settings=new PlotSettings(layout.ModelType);settings.CopyFrom(layout);validator.RefreshLists(settings);var media=new JArray();foreach(string name in validator.GetCanonicalMediaNameList(settings)){if(media.Count>=512){errors.Add("media_limit_512:"+layout.LayoutName);break;}media.Add(name);}facts["available_media"]=media;facts["media_complete"]=media.Count<512;}
     catch(Exception ex){facts["media_complete"]=false;facts["media_error"]=ex.Message;errors.Add("media_unavailable:"+layout.LayoutName);}
     if(LayoutManager.Current.CurrentLayout==layout.LayoutName)
     {
      try{using var settings=new PlotSettings(layout.ModelType);settings.CopyFrom(layout);using var info=new PlotInfo{Layout=layout.ObjectId,OverrideSettings=settings};using var validation=new PlotInfoValidator{MediaMatchingPolicy=MatchingPolicy.MatchEnabled};validation.Validate(info);facts["preflight"]=new JObject{["passed"]=info.ValidatedSettings.PlotConfigurationName==settings.PlotConfigurationName&&info.ValidatedSettings.CanonicalMediaName==settings.CanonicalMediaName,["validation_scope"]="native_device_media_only_not_content_fit_or_font_audit",["actual_device"]=info.ValidatedSettings.PlotConfigurationName,["actual_media"]=info.ValidatedSettings.CanonicalMediaName,["job_submitted"]=false};}
      catch(Exception ex){facts["preflight"]=new JObject{["passed"]=false,["error"]=ex.Message,["job_submitted"]=false};}
     }
     else facts["preflight"]=new JObject{["passed"]=JValue.CreateNull(),["reason"]="non_current_layout_not_validated",["job_submitted"]=false};
     ((JArray)result["layouts"]).Add(facts);
    }
    tx.Abort();result["complete"]=errors.Count==0;
   }
   catch(Exception ex){errors.Add(ex.Message);}
   return result;
  }
 }
}
