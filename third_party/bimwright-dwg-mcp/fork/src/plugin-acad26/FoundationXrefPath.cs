using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;
using Bimwright.Dwg.Shared.Execution;
namespace Bimwright.Dwg.Plugin {
 internal static partial class FoundationPlanWriteHandler {
  static JObject XrefPathFacts(Database db,Transaction tx) {
   var result=new JObject();
   foreach(ObjectId id in (BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead)) {
    var b=(BlockTableRecord)tx.GetObject(id,OpenMode.ForRead);if(!b.IsFromExternalReference)continue;
    if(result.Count>=128)throw new InvalidOperationException("xref_path_catalog_limit_128");
    result[b.Name]=new JObject{["name"]=b.Name,["handle"]=b.Handle.ToString(),["is_overlay"]=b.IsFromOverlayReference,["path"]=b.PathName};
   }return result;
  }
  static void SetXrefPath(Document doc,Transaction tx,JObject s,JObject map) {
   var facts=XrefPathFacts(doc.Database,tx);string name=s.Value<string>("name");
   if(facts[name]==null||FoundationDrawingPlan.Hash(facts[name])!=s.Value<string>("expected_sha256"))throw new InvalidOperationException("xref_definition_changed:"+name);
   string directory=Path.GetDirectoryName(Path.GetFullPath(doc.Name));string source=Path.GetFullPath(Path.Combine(directory,s.Value<string>("path")));
   if(!String.Equals(Path.GetDirectoryName(source),directory,StringComparison.OrdinalIgnoreCase)||!File.Exists(source))throw new InvalidOperationException("xref_source_must_exist_beside_owned_drawing");
   string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
   if(hash!=s.Value<string>("file_sha256"))throw new InvalidOperationException("xref_source_file_changed");
   var table=(BlockTable)tx.GetObject(doc.Database.BlockTableId,OpenMode.ForRead);
   var record=(BlockTableRecord)tx.GetObject(table[name],OpenMode.ForWrite);
   record.PathName=s.Value<string>("path");map["xref"]=name;map["file_sha256"]=hash;map["reload"]=false;
   // Resolve on a normal reopen; no hidden reload or viewport modification.
  }
 }
}
