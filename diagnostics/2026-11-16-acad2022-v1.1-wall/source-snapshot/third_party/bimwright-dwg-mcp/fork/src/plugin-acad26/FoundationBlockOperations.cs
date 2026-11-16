using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using Bimwright.Dwg.Plugin.Blocks;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Plugin
{
 internal static partial class FoundationPlanWriteHandler
 {
  static JObject BlockDefinitions(Database db,Transaction tx)
  {
   int detailBudget=128;var result=new JObject();foreach(ObjectId id in (BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead))
   {
    var b=(BlockTableRecord)tx.GetObject(id,OpenMode.ForRead);if(b.IsLayout||b.IsAnonymous)continue;if(result.Count>=256)throw new InvalidOperationException("named_block_catalog_limit_256");
    bool complete=!b.IsFromExternalReference&&!b.IsFromOverlayReference&&!b.IsDynamicBlock;var members=new JArray();int count=0;
    if(!complete){result[b.Name]=new JObject{["name"]=b.Name,["handle"]=b.Handle.ToString(),["complete"]=false,["entity_count"]=JValue.CreateNull(),["members"]=members,["reason"]="xref_or_dynamic_block_contents_not_read"};continue;}
    foreach(ObjectId entityId in b){count++;if(count>32||detailBudget<=0){complete=false;continue;}detailBudget--;var entity=tx.GetObject(entityId,OpenMode.ForRead) as Entity;if(entity==null){complete=false;continue;}bool supported=entity switch{Line l=>l.StartPoint.Z==0&&l.EndPoint.Z==0&&l.Thickness==0,Circle c=>c.Normal==Vector3d.ZAxis&&c.Center.Z==0&&c.Thickness==0,AttributeDefinition a=>!a.Constant&&!a.IsMTextAttributeDefinition&&a.Normal==Vector3d.ZAxis&&a.Position.Z==0&&a.Thickness==0,_=>false};if(!supported){complete=false;members.Add(new JObject{["handle"]=entity.Handle.ToString(),["type"]=entity.GetType().Name,["coverage"]="unsupported_member_geometry_not_read"});continue;}var facts=Facts(entity,tx);members.Add(facts);}
    result[b.Name]=new JObject{["name"]=b.Name,["handle"]=b.Handle.ToString(),["complete"]=complete,["entity_count"]=count,["members"]=members};
   }
   return result;
  }
  static bool ExecuteBlock(Database db,Transaction tx,JObject s,JObject map,Dictionary<string,ObjectId> refs,List<string> created)
  {
   string op=s.Value<string>("op");if(!FoundationDrawingPlan.BlockOperations.Contains(op))return false;
   var table=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead);
   if(op=="block_define")
   {
    string name=s.Value<string>("name");if(table.Has(name))throw new InvalidOperationException("block_definition_already_exists_no_overwrite");table.UpgradeOpen();table.DisableUndoRecording(false);var block=new BlockTableRecord{Name=name,Origin=Point3d.Origin};table.Add(block);tx.AddNewlyCreatedDBObject(block,true);block.DisableUndoRecording(false);
    foreach(JObject m in (JArray)s["members"])
    {
     Entity e;
     if(m.Value<string>("kind")=="line")e=new Line(Point(m["start"]),Point(m["end"]));
     else if(m.Value<string>("kind")=="circle")e=new Circle(Point(m["center"]),Vector3d.ZAxis,m.Value<double>("radius"));
     else e=new AttributeDefinition{Tag=m.Value<string>("tag"),TextString=m.Value<string>("text"),Position=Point(m["position"]),Height=m.Value<double>("height"),Constant=false,Verifiable=false};
     e.SetDatabaseDefaults(db);e.Layer="0";e.Color=Color.FromColorIndex(ColorMethod.ByAci,0);block.AppendEntity(e);tx.AddNewlyCreatedDBObject(e,true);e.DisableUndoRecording(false);
    }
    map["name"]=name;return true;
   }
   if(op=="block_insert")
   {
    string name=s.Value<string>("name");var definition=BlockDefinitions(db,tx)[name] as JObject;if(definition==null||definition.Value<bool?>("complete")!=true||FoundationDrawingPlan.Hash(definition)!=s.Value<string>("definition_sha256"))throw new InvalidOperationException("fresh_complete_simple_block_definition_required");
    var tags=((JArray)definition["members"]).Cast<JObject>().Where(e=>e.Value<string>("type")=="AttributeDefinition").Select(e=>e.Value<string>("attribute_tag")).ToArray();
    if(tags.Length>16||tags.Distinct().Count()!=tags.Length||((JObject)s["attributes"]).Properties().Any(p=>!tags.Contains(p.Name)))throw new InvalidOperationException("unique_existing_attribute_tags_required");
    var b=new BlockReference(Point(s["position"]),table[name]){ScaleFactors=new Scale3d(s.Value<double>("scale")),Rotation=FoundationDrawingPlan.NormalizeAngle(s.Value<double>("rotation")*Math.PI/180)};
    var id=Append(db,tx,b,s.Value<string>("layer"),s.Value<int>("color"));BlockAttributeService.AddAttributeReferencesAndApplyValues(tx,b,table[name],(JObject)s["attributes"]);map["entity"]=id.Handle.ToString();refs[s.Value<string>("id")]=id;created.Add(id.Handle.ToString());return true;
   }
   string handle=s["targets"][0].Value<string>("handle");var reference=tx.GetObject(Resolve(db,handle),OpenMode.ForWrite) as BlockReference;
   if(reference==null||reference.IsDynamicBlock||reference.AttributeCollection.Count>16)throw new InvalidOperationException("simple_block_reference_required");
   var record=(BlockTableRecord)tx.GetObject(reference.BlockTableRecord,OpenMode.ForRead);if(record.IsFromExternalReference||record.IsFromOverlayReference||record.IsAnonymous)throw new InvalidOperationException("local_named_block_required");
   foreach(ObjectId id in reference.AttributeCollection){var a=tx.GetObject(id,OpenMode.ForWrite) as AttributeReference;if(a==null||a.GetPersistentReactorIds().Count!=0||a.IsMTextAttribute)throw new InvalidOperationException("simple_attributes_without_reactors_required");a.DisableUndoRecording(false);}
   reference.DisableUndoRecording(false);var updated=BlockAttributeService.SetAttributes(reference,tx,(JObject)s["attributes"],true);if(updated.MissingTags.Length>0)throw new InvalidOperationException("unknown_attribute_tag");map[handle]=handle;return true;
  }
 }
}
