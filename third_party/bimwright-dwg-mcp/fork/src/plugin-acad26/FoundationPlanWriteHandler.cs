using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using Bimwright.Dwg.Plugin.Cad;
using Bimwright.Dwg.Plugin.Annotation;
using Bimwright.Dwg.Plugin.Execution;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin
{
    internal static class FoundationPlanWriteHandler
    {
        internal static PluginWriteJobResult Execute(Document doc,PluginJobSnapshot job)
        {
            if(!App.FoundationWriteEnabled||job==null||!job.IsWrite||job.ToolName!=FoundationDrawingPlan.PluginToolName||!FoundationDrawingPlan.IsCanonicalValid(job.CanonicalParametersJson))
                return PluginWriteJobResult.RejectedBeforeTransaction("foundation_plan_disabled_or_invalid","The bounded drawing plan is disabled or invalid.");
            JObject receipt=ExecutePlan(doc,job.CanonicalParametersJson);string json=receipt.ToString(Formatting.None);
            switch(receipt.Value<string>("outcome"))
            {
                case "rejected_before_write":return PluginWriteJobResult.RejectedBeforeTransaction("foundation_plan_preflight",receipt.Value<string>("error"));
                case "aborted_verified":return PluginWriteJobResult.RolledBackBeforeCommit("foundation_plan_rollback",receipt.Value<string>("error"));
                case "committed_verified":return PluginWriteJobResult.Committed(json,PluginVerificationState.Passed,json);
                case "committed_readback_failed":return PluginWriteJobResult.Committed(json,PluginVerificationState.Failed,json,"foundation_plan_readback_failed","Inspect existing handles; never repeat creation.");
                case "committed_readback_unknown":return PluginWriteJobResult.Committed(json,PluginVerificationState.Unknown,null,"foundation_plan_readback_unknown","Commit returned; query the original request.");
                default:return PluginWriteJobResult.OutcomeUnknown("foundation_plan_outcome_unknown","Retain the fence and query the original request; never replay.");
            }
        }
        static JObject Facts(Entity e,Transaction tx)=>(JObject)JObject.FromObject(CadEntityProperties.Describe(e,tx,true));
        static ObjectId Resolve(Database db,string h)
        {if(!CadHandleResolver.TryResolve(db,h,out ObjectId id,out string error))throw new InvalidOperationException(error);return id;}
        static Point3d Point(JToken a)=>new Point3d(FoundationDrawingPlan.Number(a[0]),FoundationDrawingPlan.Number(a[1]),0);
        static JArray Handles(Database db,Transaction tx)
        {
            var model=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForRead);
            var result=new List<string>();foreach(ObjectId id in model){if(result.Count==100000)throw new InvalidOperationException("first_release_model_limit_100000");result.Add(id.Handle.ToString());}return new JArray(result.OrderBy(v=>v,StringComparer.Ordinal));
        }
        static JObject ReadEntities(Database db,Transaction tx,IEnumerable<string> handles)
        {var result=new JObject();foreach(string h in handles.Distinct().OrderBy(v=>v,StringComparer.Ordinal)){var e=tx.GetObject(Resolve(db,h),OpenMode.ForRead) as Entity;if(e==null)throw new InvalidOperationException("not_entity:"+h);result[h]=Facts(e,tx);}return result;}
        static JObject LayerFacts(Database db,Transaction tx)
        {
            var result=new JObject();var table=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForRead);foreach(ObjectId id in table){var layer=(LayerTableRecord)tx.GetObject(id,OpenMode.ForRead);result[layer.Name]=new JObject{["handle"]=id.Handle.ToString(),["color_index"]=(int)layer.Color.ColorIndex,["is_off"]=layer.IsOff,["is_frozen"]=layer.IsFrozen,["is_locked"]=layer.IsLocked,["is_plottable"]=layer.IsPlottable,["linetype_handle"]=layer.LinetypeObjectId.Handle.ToString()};}return result;
        }
        static ObjectId Layer(Database db,Transaction tx,string name,int color,bool archive=false)
        {
            var table=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForRead);if(!table.Has(name)){table.UpgradeOpen();table.DisableUndoRecording(false);}
            if(!CadLayerService.TryEnsureLayer(db,tx,name,color,out ObjectId id,out bool created,out string error))throw new InvalidOperationException(error);
            var record=(LayerTableRecord)tx.GetObject(id,OpenMode.ForRead);if(created){record.UpgradeOpen();record.DisableUndoRecording(false);}
            if(record.IsLocked)throw new InvalidOperationException("locked_layer:"+name);
            if(archive)
            {
                if(!created&&(!record.IsOff||!record.IsFrozen||record.IsPlottable))throw new InvalidOperationException("archive_layer_existing_state_mismatch");
                if(created){record.UpgradeOpen();record.IsOff=true;record.IsFrozen=true;record.IsPlottable=false;}
            }
            else if(record.IsOff||record.IsFrozen)throw new InvalidOperationException("creation_layer_hidden:"+name);
            return id;
        }
        static ObjectId Append(Database db,Transaction tx,Entity e,string layer,int color)
        {
            e.LayerId=Layer(db,tx,layer,7);e.Color=Color.FromColorIndex(ColorMethod.ByAci,(short)color);
            var model=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForWrite);
            ObjectId id=model.AppendEntity(e);tx.AddNewlyCreatedDBObject(e,true);e.DisableUndoRecording(false);return id;
        }
        static Hatch Fill(Database db,Transaction tx,ObjectId boundary,string layer,int color,string pattern,double scale,double angle)
        {
            var hatch=new Hatch();hatch.SetDatabaseDefaults(db);Append(db,tx,hatch,layer,color);hatch.SetHatchPattern(HatchPatternType.PreDefined,pattern);hatch.PatternScale=scale;hatch.PatternAngle=angle;hatch.Associative=true;
            var curve=(Entity)tx.GetObject(boundary,OpenMode.ForWrite);curve.DisableUndoRecording(false);
            hatch.AppendLoop(HatchLoopTypes.External,new ObjectIdCollection{boundary});hatch.EvaluateHatch(true);return hatch;
        }
        static JObject TextInput(JObject step,bool mtext)
        {
            var p=Point(step["position"]);var input=new JObject{["text"]=step["text"],[mtext?"location":"position"]=new JObject{["x"]=p.X,["y"]=p.Y,["z"]=0},["height"]=step["height"],["rotation"]=step["rotation"]??new JValue(0)};
            if(mtext)input["width"]=step["width"];return input;
        }
        static IEnumerable<JObject> Targets(JObject plan)=>((JArray)plan["steps"]).Cast<JObject>().SelectMany(s=>s.Value<string>("op")=="set_text"?new[]{s}.Concat(((JArray)s["companions"]).Cast<JObject>()):s["targets"] is JArray targets?targets.Cast<JObject>():Enumerable.Empty<JObject>());
        static void GuardTargets(Database db,Transaction tx,JObject plan,JObject before)
        {
            var targets=Targets(plan).ToArray();var names=targets.Select(t=>t.Value<string>("handle")).ToHashSet(StringComparer.Ordinal);
            if(names.Count!=targets.Length)throw new InvalidOperationException("an_existing_handle_may_be_modified_once_per_plan");
            ObjectId model=SymbolUtilityServices.GetBlockModelSpaceId(db);
            foreach(var target in targets)
            {
                string h=target.Value<string>("handle");var entity=tx.GetObject(Resolve(db,h),OpenMode.ForRead) as Entity;
                if(entity==null||entity.OwnerId!=model||entity is not (Line or Circle or DBText or MText or Leader or Hatch))throw new InvalidOperationException("unsupported_existing_model_entity:"+h);
                if(((LayerTableRecord)tx.GetObject(entity.LayerId,OpenMode.ForRead)).IsLocked)throw new InvalidOperationException("locked_source_layer:"+h);
                if(FoundationDrawingPlan.Hash(before[h])!=target.Value<string>("expected_sha256"))throw new InvalidOperationException("entity_changed:"+h);
                foreach(ObjectId reactor in entity.GetPersistentReactorIds())
                {var other=tx.GetObject(reactor,OpenMode.ForRead) as Entity;if(other==null||!names.Contains(other.Handle.ToString()))throw new InvalidOperationException("include_complete_associated_group:"+h);}
                if(entity is Leader leader&&!leader.Annotation.IsNull&&!names.Contains(leader.Annotation.Handle.ToString()))throw new InvalidOperationException("include_leader_annotation:"+h);
                if(entity is Hatch hatch)
                {
                    if(!hatch.Associative||hatch.NumberOfLoops!=1)throw new InvalidOperationException("only_associated_single_circle_fill_group_supported");
                    var ids=hatch.GetAssociatedObjectIdsAt(0);if(ids.Count!=1||!names.Contains(ids[0].Handle.ToString())||tx.GetObject(ids[0],OpenMode.ForRead) is not Circle)throw new InvalidOperationException("include_dot_circle_boundary");
                }
            }
        }
        static void MoveEntity(Entity entity,JObject old,JArray delta,Transaction tx)
        {
            var d=new Vector3d(FoundationDrawingPlan.Number(delta[0]),FoundationDrawingPlan.Number(delta[1]),0);
            Point3d Shift(JToken p)=>new Point3d(p.Value<double>("x"),p.Value<double>("y"),p.Value<double>("z"))+d;
            switch(entity)
            {
                case Line line:line.StartPoint=Shift(old["start"]);line.EndPoint=Shift(old["end"]);break;
                case Circle circle:circle.Center=Shift(old["center"]);break;
                case DBText text:text.Position=Shift(old["position"]);break;
                case MText text:text.Location=Shift(old["location"]);break;
                case Leader leader:for(int i=0;i<leader.NumVertices;i++)leader.SetVertexAt(i,Shift(old["vertices_wcs"][i]));break;
                case Hatch:break; // Rebuild its circle association after the boundary has its exact planned position.
                default:throw new InvalidOperationException("unsupported_move");
            }
        }
        internal static JObject ExecutePlan(Document doc,string canonicalPlan,Action<string> checkpoint=null)
        {
            JObject plan=FoundationDrawingPlan.Parse(canonicalPlan);
            if(((JArray)plan["steps"]).Count==1&&plan["steps"][0].Value<string>("op")=="save")return SaveDocument(doc,plan,canonicalPlan);
            JObject before=new JObject(),after=new JObject(),objects=new JObject(),layers=new JObject(),allLayersBefore=null,allLayersAfter=null;JArray modelBefore=null,modelAfter=null;
            Transaction tx=null;bool opened=false,invoked=false,committed=false;string outcome="unknown",error=null;var created=new List<string>();
            using(doc.LockDocument())try
            {
                if(doc.IsReadOnly||!doc.Database.UndoRecording)throw new InvalidOperationException("document_read_only_or_undo_disabled");
                int units=Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("INSUNITS"));if(units!=0&&units!=6)throw new InvalidOperationException("first_release_requires_metres_or_explicit_unitless_metres");
                tx=doc.Database.TransactionManager.StartTransaction();modelBefore=Handles(doc.Database,tx);allLayersBefore=LayerFacts(doc.Database,tx);
                before=ReadEntities(doc.Database,tx,Targets(plan).Select(v=>v.Value<string>("handle")));GuardTargets(doc.Database,tx,plan,before);
                // All modifications share one transaction. Symbol/table changes and associated boundaries participate in undo.
                var db=doc.Database;var refs=new Dictionary<string,ObjectId>();
                foreach(JObject step in (JArray)plan["steps"])
                {
                    string id=step.Value<string>("id"),op=step.Value<string>("op");var map=new JObject();objects[id]=map;opened=true;
                    string layer=step.Value<string>("layer");int color=step.Value<int?>("color")??256;
                    ObjectId Add(Entity e,string role="entity",int? explicitColor=null)
                    {ObjectId oid=Append(db,tx,e,layer,explicitColor??color);map[role]=oid.Handle.ToString();created.Add(oid.Handle.ToString());return oid;}
                    switch(op)
                    {
                        case "layer":
                            string name=step.Value<string>("name");var lid=Layer(db,tx,name,step.Value<int>("color"));var lr=(LayerTableRecord)tx.GetObject(lid,OpenMode.ForRead);
                            if(lr.Color.ColorIndex!=step.Value<int>("color"))throw new InvalidOperationException("existing_layer_color_mismatch:"+name);map["layer"]=name;layers[name]=new JObject{["color_index"]=(int)lr.Color.ColorIndex,["is_off"]=lr.IsOff,["is_frozen"]=lr.IsFrozen,["is_plottable"]=lr.IsPlottable};break;
                        case "line":refs[id]=Add(new Line(Point(step["start"]),Point(step["end"])));break;
                        case "circle":refs[id]=Add(new Circle(Point(step["center"]),Vector3d.ZAxis,step.Value<double>("radius")));break;
                        case "polyline":
                            var poly=new Polyline();poly.SetDatabaseDefaults(db);var points=(JArray)step["vertices"];for(int i=0;i<points.Count;i++){var p=(JArray)points[i];poly.AddVertexAt(i,new Point2d(p[0].Value<double>(),p[1].Value<double>()),p.Count==3?p[2].Value<double>():0,0,0);}poly.Closed=step.Value<bool>("closed");refs[id]=Add(poly);break;
                        case "text":
                            if(!AnnotationEntityFactory.TryCreateText(TextInput(step,false),out DBText text,out string textError))throw new InvalidOperationException(textError);refs[id]=Add(text);break;
                        case "mtext":
                            if(!AnnotationEntityFactory.TryCreateMText(TextInput(step,true),out MText mtext,out string mtError))throw new InvalidOperationException(mtError);refs[id]=Add(mtext);break;
                        case "hatch":
                            if(!refs.TryGetValue(step.Value<string>("boundary"),out ObjectId boundary))throw new InvalidOperationException("boundary_reference_unavailable");
                            var curve=tx.GetObject(boundary,OpenMode.ForRead);if(curve is not Circle&&curve is not Polyline{Closed:true})throw new InvalidOperationException("closed_polyline_or_circle_required");
                            var fill=Fill(db,tx,boundary,layer,color,step.Value<string>("pattern"),step.Value<double>("scale"),step.Value<double>("angle"));map["entity"]=fill.Handle.ToString();created.Add(fill.Handle.ToString());refs[id]=fill.ObjectId;break;
                        case "callout":
                            if(!AnnotationEntityFactory.TryCreateMText(TextInput(step,true),out MText label,out string labelError))throw new InvalidOperationException(labelError);
                            label.Attachment=AttachmentPoint.BottomLeft;ObjectId labelId=Add(label,"text");
                            var leader=new Leader();leader.SetDatabaseDefaults(db);leader.HasArrowHead=false;leader.IsSplined=false;leader.Dimtxt=step.Value<double>("height");leader.Dimgap=0;leader.Dimtad=0;
                            foreach(var vertex in (JArray)step["vertices"])if(!leader.AppendVertex(Point(vertex)))throw new InvalidOperationException("leader_vertex_failed");Add(leader,"leader");leader.Annotation=labelId;
                            ObjectId dot=Add(new Circle(Point(step["vertices"][0]),Vector3d.ZAxis,step.Value<double>("dot_radius")),"dot_boundary",2);
                            var dotFill=Fill(db,tx,dot,layer,2,"SOLID",1,0);map["dot_fill"]=dotFill.Handle.ToString();created.Add(dotFill.Handle.ToString());break;
                        case "set_text":
                            var existing=(Entity)tx.GetObject(Resolve(db,step.Value<string>("handle")),OpenMode.ForWrite);existing.DisableUndoRecording(false);
                            if(existing is DBText oldText)oldText.TextString=step.Value<string>("text");else if(existing is MText oldMtext)oldMtext.Contents=step.Value<string>("text");else throw new InvalidOperationException("text_entity_required");map["entity"]=existing.Handle.ToString();break;
                        case "move":case "archive":
                            var targets=(JArray)step["targets"];
                            ObjectId archiveLayer=op=="archive"?Layer(db,tx,layer,7,true):ObjectId.Null;
                            foreach(JObject target in targets)
                            {
                                var entity=(Entity)tx.GetObject(Resolve(db,target.Value<string>("handle")),OpenMode.ForWrite);entity.DisableUndoRecording(false);
                                if(op=="archive")entity.LayerId=archiveLayer;else MoveEntity(entity,(JObject)before[entity.Handle.ToString()],(JArray)step["delta"],tx);
                                map[entity.Handle.ToString()]=entity.Handle.ToString();
                            }
                            if(op=="move")foreach(JObject target in targets)
                            {
                                var entity=(Entity)tx.GetObject(Resolve(db,target.Value<string>("handle")),OpenMode.ForWrite);
                                if(entity is Hatch movedFill){var ids=movedFill.GetAssociatedObjectIdsAt(0);movedFill.RemoveLoopAt(0);movedFill.Associative=true;movedFill.AppendLoop(HatchLoopTypes.External,ids);movedFill.EvaluateHatch(true);}
                            }
                            if(op=="archive"){var ar=(LayerTableRecord)tx.GetObject(archiveLayer,OpenMode.ForRead);layers[layer]=new JObject{["is_off"]=ar.IsOff,["is_frozen"]=ar.IsFrozen,["is_plottable"]=ar.IsPlottable};}break;
                    }
                    checkpoint?.Invoke("after_step:"+id);
                }
                var observed=before.Properties().Select(p=>p.Name).Concat(created).ToArray();after=ReadEntities(db,tx,observed);modelAfter=Handles(db,tx);allLayersAfter=LayerFacts(db,tx);
                JObject Receipt()=>Build(plan,before,after,objects,layers,allLayersBefore,allLayersAfter,modelBefore,modelAfter,created,"committed_verified",true,true,null);
                if(!FoundationDrawingPlan.VerifyReceipt(Receipt(),canonicalPlan)||!IdentitySetMatches(modelBefore,modelAfter,created))throw new InvalidOperationException("plan_precommit_verification_failed");
                invoked=true;tx.Commit();committed=true;tx.Dispose();tx=null;checkpoint?.Invoke("after_commit");
                using(var read=db.TransactionManager.StartTransaction()){after=ReadEntities(db,read,observed);modelAfter=Handles(db,read);allLayersAfter=LayerFacts(db,read);read.Abort();}
                outcome=FoundationDrawingPlan.VerifyReceipt(Receipt(),canonicalPlan)&&IdentitySetMatches(modelBefore,modelAfter,created)?"committed_verified":"committed_readback_failed";
            }
            catch(Exception ex)
            {
                error=ex.Message;
                if(committed)outcome="committed_readback_unknown";
                else if(invoked)outcome="commit_outcome_unknown";
                else if(!opened)outcome="rejected_before_write";
                else
                {
                    try
                    {tx?.Abort();tx?.Dispose();tx=null;using var read=doc.Database.TransactionManager.StartTransaction();after=ReadEntities(doc.Database,read,before.Properties().Select(p=>p.Name));modelAfter=Handles(doc.Database,read);allLayersAfter=LayerFacts(doc.Database,read);read.Abort();outcome=JToken.DeepEquals(before,after)&&JToken.DeepEquals(allLayersBefore,allLayersAfter)&&JToken.DeepEquals(modelBefore,modelAfter)?"aborted_verified":"rollback_outcome_unknown";}
                    catch{outcome="rollback_outcome_unknown";}
                }
            }
            finally{tx?.Dispose();}
            return Build(plan,before,after,objects,layers,allLayersBefore,allLayersAfter,modelBefore,modelAfter,created,outcome,invoked,committed,error);
        }
        static bool IdentitySetMatches(JArray before,JArray after,IEnumerable<string> created)=>before!=null&&after!=null&&after.Values<string>().ToHashSet().SetEquals(before.Values<string>().Concat(created))&&created.Distinct().Count()==created.Count();
        static JObject SaveDocument(Document doc,JObject plan,string canonicalPlan)
        {
            JObject before=new JObject(),after=new JObject(),layersBefore=null,layersAfter=null;JArray modelBefore=null,modelAfter=null;
            var step=(JObject)plan["steps"][0];var map=new JObject();var objects=new JObject{[step.Value<string>("id")]=map};bool invoked=false,returned=false;string outcome="unknown",error=null,fileSha=null;int dbmod=-1;
            using(doc.LockDocument())try
            {
                if(doc.IsReadOnly||!System.IO.File.Exists(doc.Name))throw new InvalidOperationException("save_requires_writable_existing_local_dwg");
                string[] handles=((JArray)step["expected"]).Cast<JObject>().Select(t=>t.Value<string>("handle")).ToArray();if(handles.Distinct().Count()!=handles.Length)throw new InvalidOperationException("duplicate_save_handle");
                using(var read=doc.Database.TransactionManager.StartTransaction())
                {
                    before=ReadEntities(doc.Database,read,handles);modelBefore=Handles(doc.Database,read);layersBefore=LayerFacts(doc.Database,read);
                    foreach(JObject t in (JArray)step["expected"])if(FoundationDrawingPlan.Hash(before[t.Value<string>("handle")])!=t.Value<string>("expected_sha256"))throw new InvalidOperationException("save_entity_changed:"+t.Value<string>("handle"));read.Abort();
                }
                foreach(string handle in handles)map[handle]=handle;
                // The host's native document Save is used, retaining its actual current path and version.
                dynamic acadDocument=doc.GetAcadDocument();invoked=true;acadDocument.Save();returned=true;
                dbmod=Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("DBMOD"));
                using(var sha=System.Security.Cryptography.SHA256.Create())using(var stream=new System.IO.FileStream(doc.Name,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.ReadWrite|System.IO.FileShare.Delete))fileSha=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
                using(var disk=new Database(false,true))
                {
                    disk.ReadDwgFile(doc.Name,FileOpenMode.OpenForReadAndAllShare,true,null);disk.CloseInput(true);
                    using var read=disk.TransactionManager.StartTransaction();after=ReadEntities(disk,read,handles);modelAfter=Handles(disk,read);layersAfter=LayerFacts(disk,read);read.Abort();
                }
                outcome="committed_verified";
                var candidate=Build(plan,before,after,objects,new JObject(),layersBefore,layersAfter,modelBefore,modelAfter,Array.Empty<string>(),outcome,invoked,returned,null);candidate["saved_file_sha256"]=fileSha;candidate["dbmod_after_save"]=dbmod;
                if(!FoundationDrawingPlan.VerifyReceipt(candidate,canonicalPlan))outcome="committed_readback_failed";
            }
            catch(Exception ex){error=ex.Message;outcome=returned?"committed_readback_unknown":invoked?"commit_outcome_unknown":"rejected_before_write";}
            var result=Build(plan,before,after,objects,new JObject(),layersBefore,layersAfter,modelBefore,modelAfter,Array.Empty<string>(),outcome,invoked,returned,error);result["saved_file_sha256"]=fileSha==null?JValue.CreateNull():new JValue(fileSha);result["dbmod_after_save"]=dbmod;return result;
        }
        static JObject Build(JObject plan,JObject before,JObject after,JObject objects,JObject layers,JObject allLayersBefore,JObject allLayersAfter,JArray modelBefore,JArray modelAfter,IEnumerable<string> created,string outcome,bool invoked,bool committed,string error)=>new JObject{
            ["schema"]="foundation-plan-receipt-v1",["plan_sha256"]=FoundationDrawingPlan.Hash(plan),["outcome"]=outcome,["effects"]=committed?"committed":outcome=="aborted_verified"||outcome=="rejected_before_write"?"none":"unknown",["commit_invoked"]=invoked,["commit_returned"]=committed,["error"]=error==null?JValue.CreateNull():new JValue(error),["objects"]=objects,["created_handles"]=new JArray(created),["before"]=before,["after"]=after,["layers"]=layers,["all_layers_before"]=allLayersBefore,["all_layers_after"]=allLayersAfter,["model_handles_before"]=modelBefore,["model_handles_after"]=modelAfter,["journal_finalized"]=false,["fence_release_authorized"]=false};
    }
}
