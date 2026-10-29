using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Plugin
{
 internal static partial class FoundationPlanWriteHandler
 {
  internal static JObject RasterReferenceStatus(Document doc,RasterImage image,Transaction tx)
  {
   var def=(RasterImageDef)tx.GetObject(image.ImageDefId,OpenMode.ForRead);
   string active=null,error=null;try{active=def.ActiveFileName;}catch(Exception ex){error=ex.Message;}
   string source=def.SourceFileName,resolved=null;
   try{resolved=Path.IsPathFullyQualified(source)?source:Path.GetFullPath(Path.Combine(Path.GetDirectoryName(doc.Name),source));}catch(Exception ex){error=ex.Message;}
   return new JObject{["kind"]="native_raster_reference",["source_file"]=source,["active_file"]=active,["resolved_against_drawing"]=resolved,["source_exists"]=resolved!=null&&File.Exists(resolved),["native_is_loaded"]=def.IsLoaded,["read_error"]=error,["note"]="Native loaded state does not prove visible tiles or geographic alignment; path repair never calibrates coordinates."};
  }
 }
}
