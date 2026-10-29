using System;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Plugin
{
 internal static partial class FoundationPlanWriteHandler
 {
  static JObject ResourceCatalog(Database db,Transaction tx)
  {
   var blocks=new JArray();var lineTypes=new JArray();bool blocksComplete=true,lineTypesComplete=true;
   foreach(ObjectId id in (BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead))
   {
    var b=(BlockTableRecord)tx.GetObject(id,OpenMode.ForRead);if(b.IsLayout||b.IsAnonymous)continue;
    if(blocks.Count>=256){blocksComplete=false;break;}
    blocks.Add(new JObject{["name"]=b.Name,["handle"]=b.Handle.ToString(),["is_dynamic"]=b.IsDynamicBlock,["is_xref"]=b.IsFromExternalReference,["is_overlay"]=b.IsFromOverlayReference,["xref_status"]=b.XrefStatus.ToString(),["path"]=b.PathName??"",["content_coverage"]="definition_metadata_only_no_nested_geometry"});
   }
   foreach(ObjectId id in (LinetypeTable)tx.GetObject(db.LinetypeTableId,OpenMode.ForRead))
   {if(lineTypes.Count>=256){lineTypesComplete=false;break;}var l=(LinetypeTableRecord)tx.GetObject(id,OpenMode.ForRead);lineTypes.Add(new JObject{["name"]=l.Name,["handle"]=l.Handle.ToString(),["description"]=l.AsciiDescription,["pattern_length"]=l.PatternLength,["is_dependent"]=l.IsDependent});}
   return new JObject{["observation"]="bind_time",["blocks"]=blocks,["blocks_complete"]=blocksComplete,["linetypes"]=lineTypes,["linetypes_complete"]=lineTypesComplete,["xref_editing_enabled"]=false,["xref_path_repair_enabled"]=true};
  }
 }
}
