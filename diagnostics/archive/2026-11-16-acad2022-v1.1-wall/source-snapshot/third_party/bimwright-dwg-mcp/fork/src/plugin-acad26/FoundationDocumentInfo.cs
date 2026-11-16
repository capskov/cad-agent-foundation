using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Core.Application;
namespace Bimwright.Dwg.Plugin
{
 internal static class FoundationDocumentInfo
 {
  internal static JObject Read(Document doc)
  {
   var info=new JObject{["schema"]="foundation-document-info-v1",["observation"]="bind_time",["reported_at_utc"]=System.DateTimeOffset.UtcNow.ToString("O"),["target_space"]="ModelSpace",["loaded_plugin_mvid"]=typeof(App).Module.ModuleVersionId.ToString(),["native_write_enabled"]=App.FoundationWriteEnabled};
   var errors=new JArray();
   void Add(string name,System.Func<object> get){try{info[name]=JToken.FromObject(get());}catch{info[name]=JValue.CreateNull();errors.Add(name);}}
   Add("document_read_only",()=>doc.IsReadOnly);Add("database_undo_recording",()=>doc.Database.UndoRecording);
   Add("current_layout",()=>LayoutManager.Current.CurrentLayout);Add("document_count",()=>AcApp.DocumentManager.Count);
   Add("units_code",()=>AcApp.GetSystemVariable("INSUNITS"));Add("dbmod",()=>AcApp.GetSystemVariable("DBMOD"));Add("command_active",()=>AcApp.GetSystemVariable("CMDACTIVE"));
   Add("current_space",()=>doc.Database.TileMode?"ModelSpace":System.Convert.ToInt32(AcApp.GetSystemVariable("CVPORT"))>1?"ModelViewport":"PaperSpace");
   Add("main_window_handle",()=>AcApp.MainWindow.Handle.ToInt64());
   Add("secureload",()=>AcApp.GetSystemVariable("SECURELOAD"));Add("proxyshow",()=>AcApp.GetSystemVariable("PROXYSHOW"));Add("proxynotice",()=>AcApp.GetSystemVariable("PROXYNOTICE"));
   Add("foundation_catalog",()=>FoundationPlanWriteHandler.ReadCatalog(doc));
   info["unknown_fields"]=errors;return info;
  }
 }
}
