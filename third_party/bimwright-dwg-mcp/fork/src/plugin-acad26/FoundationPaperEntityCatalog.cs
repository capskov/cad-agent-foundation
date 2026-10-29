using System;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;
using Bimwright.Dwg.Plugin.Cad;
namespace Bimwright.Dwg.Plugin {
 internal static partial class FoundationPlanWriteHandler {
  static JObject PaperBlockFacts(Database db,Transaction tx){var all=PaperEntityCatalog(db,tx);var result=new JObject();foreach(var l in all.Properties())foreach(JObject e in (JArray)l.Value["entities"])if(e.Value<string>("type")=="BlockReference"){var f=(JObject)e.DeepClone();f.Remove("extents");result[e.Value<string>("handle")]=f;}return result;}
  static void CopyPaperFrame(Database db,Transaction tx,JObject step,JObject map,System.Collections.Generic.List<string> created){string h=step.Value<string>("source_handle");var f=PaperBlockFacts(db,tx)[h];if(f==null||Bimwright.Dwg.Shared.Execution.FoundationDrawingPlan.Hash(f)!=step.Value<string>("expected_sha256")||f.Value<string>("layout_name")!=step.Value<string>("layout"))throw new InvalidOperationException("paper_frame_stale_or_wrong_layout");var original=tx.GetObject(Resolve(db,h),OpenMode.ForRead) as BlockReference;if(original==null||original.AttributeCollection.Count!=0)throw new InvalidOperationException("plain_existing_xref_frame_required");var definition=(BlockTableRecord)tx.GetObject(original.BlockTableRecord,OpenMode.ForRead);if(!definition.IsFromExternalReference&&!definition.IsFromOverlayReference)throw new InvalidOperationException("existing_xref_frame_only");var owner=(BlockTableRecord)tx.GetObject(original.OwnerId,OpenMode.ForWrite);if(!owner.IsLayout||owner.ObjectId==SymbolUtilityServices.GetBlockModelSpaceId(db))throw new InvalidOperationException("paper_layout_owner_required");var copy=(BlockReference)original.Clone();copy.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(new Autodesk.AutoCAD.Geometry.Vector3d(step["delta"][0].Value<double>(),step["delta"][1].Value<double>(),0)));var id=owner.AppendEntity(copy);tx.AddNewlyCreatedDBObject(copy,true);created.Add(id.Handle.ToString());map["entity"]=id.Handle.ToString();}
  static JObject PaperTextFacts(Database db,Transaction tx){var all=PaperEntityCatalog(db,tx);var result=new JObject();foreach(var l in all.Properties())foreach(JObject e in (JArray)l.Value["entities"])if(e.Value<string>("type") is "DBText" or "MText")result[e.Value<string>("handle")]=e.DeepClone();return result;}
  static void SetPaperText(Database db,Transaction tx,JObject step,JObject map){string h=step.Value<string>("paper_handle");var f=PaperTextFacts(db,tx)[h];if(f==null||Bimwright.Dwg.Shared.Execution.FoundationDrawingPlan.Hash(f)!=step.Value<string>("expected_sha256")||f.Value<string>("layout_name")!=step.Value<string>("layout"))throw new InvalidOperationException("paper_text_stale_or_wrong_layout");var e=tx.GetObject(Resolve(db,h),OpenMode.ForWrite);if(e is DBText d)d.TextString=step.Value<string>("text");else if(e is MText m)m.Contents=step.Value<string>("text");else throw new InvalidOperationException("paper_text_only");map["paper_text"]=h;}
  static JObject OverallPaperViewports(Database db,Transaction tx){var r=new JObject();foreach(DBDictionaryEntry ent in (DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead)){var l=(Layout)tx.GetObject(ent.Value,OpenMode.ForRead);if(l.ModelType)continue;var ids=l.GetViewports();if(ids.Count>0)r[ids[0].Handle.ToString()]=l.LayoutName;}return r;}
  static JObject PaperViewportFacts(Database db,Transaction tx){var all=PaperEntityCatalog(db,tx);var overall=OverallPaperViewports(db,tx);var result=new JObject();foreach(var l in all.Properties())foreach(JObject e in (JArray)l.Value["entities"])if(e.Value<string>("type")=="Viewport"&&overall[e.Value<string>("handle")]==null){var f=(JObject)e.DeepClone();f.Remove("viewport_number");result[e.Value<string>("handle")]=f;}return result;}
  static void SetPaperViewport(Database db,Transaction tx,JObject step,JObject map){
   var facts=PaperViewportFacts(db,tx);string h=step.Value<string>("viewport_handle");if(facts[h]==null||Bimwright.Dwg.Shared.Execution.FoundationDrawingPlan.Hash(facts[h])!=step.Value<string>("expected_sha256")||facts[h].Value<string>("layout_name")!=step.Value<string>("layout"))throw new InvalidOperationException("paper_viewport_stale_or_wrong_layout");
   var vp=tx.GetObject(Resolve(db,h),OpenMode.ForWrite) as Viewport;if(vp==null||OverallPaperViewports(db,tx)[h]!=null||vp.PerspectiveOn||Math.Abs(vp.TwistAngle)>1e-10||(vp.ViewDirection-Autodesk.AutoCAD.Geometry.Vector3d.ZAxis).Length>1e-10)throw new InvalidOperationException("only_existing_orthographic_untwisted_paper_viewport_supported");
   vp.Locked=false;if(step["layer"]!=null){var table=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForRead);string name=step.Value<string>("layer");if(!table.Has(name))throw new InvalidOperationException("nonplot_viewport_layer_missing");var lr=(LayerTableRecord)tx.GetObject(table[name],OpenMode.ForRead);if(lr.IsPlottable||lr.IsLocked||lr.IsFrozen||lr.IsOff)throw new InvalidOperationException("explicit_visible_unlocked_nonplot_viewport_layer_required");vp.LayerId=table[name];}if(step["width_paper"]!=null)vp.Width=step.Value<double>("width_paper");if(step["height_paper"]!=null)vp.Height=step.Value<double>("height_paper");vp.CustomScale=vp.Height/step.Value<double>("view_height");vp.ViewCenter=new Autodesk.AutoCAD.Geometry.Point2d(step["view_center"][0].Value<double>(),step["view_center"][1].Value<double>());vp.Locked=step.Value<bool>("locked");map["viewport"]=h;
  }
  // Read-only paper entities. No explode, viewport activation or implicit writes.
  static JObject PaperEntityCatalog(Database db,Transaction tx) {
   var result=new JObject();int total=0;
   foreach(DBDictionaryEntry entry in (DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead)) {
    var layout=(Layout)tx.GetObject(entry.Value,OpenMode.ForRead);if(layout.ModelType)continue;
    var rows=new JArray();bool complete=true;
    foreach(ObjectId id in (BlockTableRecord)tx.GetObject(layout.BlockTableRecordId,OpenMode.ForRead)) {
     if(rows.Count>=512||total>=1024){complete=false;break;}
     var entity=tx.GetObject(id,OpenMode.ForRead) as Entity;if(entity==null)continue;
     var row=JObject.FromObject(CadEntityProperties.Describe(entity,tx,true));
     if(entity is Viewport v) {
      row["visible"]=v.Visible;row["viewport_number"]=v.Number;row["center_paper"]=new JArray(v.CenterPoint.X,v.CenterPoint.Y,v.CenterPoint.Z);row["width_paper"]=v.Width;row["height_paper"]=v.Height;row["custom_scale"]=v.CustomScale;row["locked"]=v.Locked;row["on"]=v.On;row["view_target"]=new JArray(v.ViewTarget.X,v.ViewTarget.Y,v.ViewTarget.Z);row["view_center"]=new JArray(v.ViewCenter.X,v.ViewCenter.Y);row["view_height"]=v.ViewHeight;
     }
     row["visible"]=entity.Visible;rows.Add(row);total++;
    }
    result[layout.LayoutName]=new JObject{["space_handle"]=layout.BlockTableRecordId.Handle.ToString(),["complete"]=complete,["entities"]=rows};
   }return result;
  }
 }
}
