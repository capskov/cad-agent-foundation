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
    internal static partial class FoundationPlanWriteHandler
    {
        internal static PluginWriteJobResult Execute(Document doc,PluginJobSnapshot job)
        {
            if(!App.FoundationWriteEnabled||job==null||!job.IsWrite||job.ToolName!=FoundationDrawingPlan.PluginToolName||!FoundationDrawingPlan.IsCanonicalValid(job.CanonicalParametersJson))
                return PluginWriteJobResult.RejectedBeforeTransaction("foundation_plan_disabled_or_invalid","The bounded drawing plan is disabled or invalid.");
            var coordinatePlan=FoundationDrawingPlan.Parse(job.CanonicalParametersJson);
            int coordinateUnits=Convert.ToInt32(doc.Database.Insunits);
            if(!FoundationDrawingPlan.UnitMetadataMatches(coordinatePlan,coordinateUnits))
                return PluginWriteJobResult.RejectedBeforeTransaction("drawing_unit_metadata_mismatch","The opted-in unit metadata check or legacy metres declaration does not match; no transaction opened. Use raw_drawing_units for ordinary raw-coordinate operations.");
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
        static ObjectId Append(Database db,Transaction tx,Entity e,string layer,int color,ObjectId spaceId=default)
        {
            e.LayerId=Layer(db,tx,layer,7);e.Color=Color.FromColorIndex(ColorMethod.ByAci,(short)color);
            var model=(BlockTableRecord)tx.GetObject(spaceId.IsNull?SymbolUtilityServices.GetBlockModelSpaceId(db):spaceId,OpenMode.ForWrite);
            ObjectId id=model.AppendEntity(e);tx.AddNewlyCreatedDBObject(e,true);e.DisableUndoRecording(false);return id;
        }
        static Hatch Fill(Database db,Transaction tx,ObjectId boundary,string layer,int color,string pattern,double scale,double angle,Hatch template=null)
        {
            string stage="clone";
            try
            {
                var hatch=template==null?new Hatch():(Hatch)template.Clone();
                stage="clear_loops";if(template==null)hatch.SetDatabaseDefaults(db);else{hatch.Associative=false;while(hatch.NumberOfLoops>0)hatch.RemoveLoopAt(0);}
                stage="append";Append(db,tx,hatch,layer,color,tx.GetObject(boundary,OpenMode.ForRead).OwnerId);
                stage="pattern";if(template==null)hatch.SetHatchPattern(HatchPatternType.PreDefined,pattern);
                // Reassigning an identical scale can force AutoCAD to look up a missing custom PAT.
                stage="scale";if(hatch.PatternScale!=scale)hatch.PatternScale=scale;
                stage="angle";if(hatch.PatternAngle!=angle)hatch.PatternAngle=angle;
                stage="association";hatch.Associative=true;
                var curve=(Entity)tx.GetObject(boundary,OpenMode.ForWrite);curve.DisableUndoRecording(false);
                stage="boundary";hatch.AppendLoop(HatchLoopTypes.External,new ObjectIdCollection{boundary});
                stage="evaluate";hatch.EvaluateHatch(true);return hatch;
            }
            catch(Exception ex){throw new InvalidOperationException("hatch_"+stage+": "+ex.Message,ex);}
        }
        static JObject TextInput(JObject step,bool mtext)
        {
            var p=Point(step["position"]);var input=new JObject{["text"]=step["text"],[mtext?"location":"position"]=new JObject{["x"]=p.X,["y"]=p.Y,["z"]=0},["height"]=step["height"],["rotation"]=step["rotation"]??new JValue(0)};
            if(mtext)input["width"]=step["width"];return input;
        }
        static IEnumerable<JObject> Targets(JObject plan)
        {
            foreach(JObject s in (JArray)plan["steps"])
            {
                if(s.Value<string>("op")=="hatch"&&s["template"] is JObject template)yield return template;
                if(s.Value<string>("op") is "set_callout" or "set_callout_format")
                {yield return s;yield return new JObject{["handle"]=s["text_handle"],["expected_sha256"]=s["text_expected_sha256"]};if(s["dot_companions"] is JArray ds)foreach(JObject d in ds)yield return d;}
                else if(s.Value<string>("op") is "set_text" or "set_polyline")
                {yield return s;foreach(JObject c in (JArray)s["companions"])yield return c;}
                else if(s["targets"] is JArray targets)foreach(JObject target in targets)yield return target;
            }
        }
        static void GuardTargets(Database db,Transaction tx,JObject plan,JObject before)
        {
            var targets=Targets(plan).ToArray();var names=targets.Select(t=>t.Value<string>("handle")).ToHashSet(StringComparer.Ordinal);
            var phase2Names=((JArray)plan["steps"]).Cast<JObject>().Where(s=>(FoundationDrawingPlan.Phase2Operations.Contains(s.Value<string>("op"))||s.Value<string>("op")=="set_block_attributes")).SelectMany(s=>(s["targets"] as JArray??new JArray()).Cast<JObject>()).Select(t=>t.Value<string>("handle")).ToHashSet(StringComparer.Ordinal);
            var archiveNames=((JArray)plan["steps"]).Cast<JObject>().Where(s=>s.Value<string>("op")=="archive").SelectMany(s=>((JArray)s["targets"]).Cast<JObject>()).Select(t=>t.Value<string>("handle")).ToHashSet(StringComparer.Ordinal);
            var boundaryNames=((JArray)plan["steps"]).Cast<JObject>().Where(s=>s.Value<string>("op")=="set_polyline").SelectMany(s=>new[]{s}.Concat(((JArray)s["companions"]).Cast<JObject>())).Select(t=>t.Value<string>("handle")).ToHashSet(StringComparer.Ordinal);
            var propertyNames=((JArray)plan["steps"]).Cast<JObject>().Where(s=>s.Value<string>("op")=="set_properties").SelectMany(s=>((JArray)s["targets"]).Cast<JObject>()).Select(t=>t.Value<string>("handle")).ToHashSet(StringComparer.Ordinal);
            var hatchFormatNames=((JArray)plan["steps"]).Cast<JObject>().Where(s=>s.Value<string>("op")=="set_hatch_pattern").SelectMany(s=>((JArray)s["targets"]).Cast<JObject>()).Select(t=>t.Value<string>("handle")).ToHashSet(StringComparer.Ordinal);
            var transformNames=((JArray)plan["steps"]).Cast<JObject>().Where(s=>s.Value<string>("op") is "rotate" or "scale" or "copy").SelectMany(s=>((JArray)s["targets"]).Cast<JObject>()).Select(t=>t.Value<string>("handle")).ToHashSet(StringComparer.Ordinal);
            if(names.Count!=targets.Length)throw new InvalidOperationException("an_existing_handle_may_be_modified_once_per_plan");
            ObjectId model=SymbolUtilityServices.GetBlockModelSpaceId(db);
            foreach(var target in targets)
            {
                string h=target.Value<string>("handle");var entity=tx.GetObject(Resolve(db,h),OpenMode.ForRead) as Entity;
                var owner=entity==null?null:tx.GetObject(entity.OwnerId,OpenMode.ForRead) as BlockTableRecord;
                if(entity==null||owner==null||!owner.IsLayout||entity.OwnerId!=model||(entity is not (Line or Circle or DBText or MText or Leader or Hatch)&&!(phase2Names.Contains(h)&&entity is (Polyline or Arc or Ellipse or DBPoint or Table or BlockReference or RasterImage or RotatedDimension or AlignedDimension or RadialDimension or DiametricDimension))&&!(archiveNames.Contains(h)&&entity is (Polyline or RotatedDimension or AlignedDimension or RadialDimension or DiametricDimension))&&!(boundaryNames.Contains(h)&&entity is Polyline{Closed:true})&&!(transformNames.Contains(h)&&entity is Polyline)))throw new InvalidOperationException("unsupported_existing_entity_or_space:"+h);
                if(((LayerTableRecord)tx.GetObject(entity.LayerId,OpenMode.ForRead)).IsLocked)throw new InvalidOperationException("locked_source_layer:"+h);
                if(transformNames.Contains(h))
                {
                    bool planar=entity switch{Line l=>l.StartPoint.Z==0&&l.EndPoint.Z==0&&l.Thickness==0,Circle c=>c.Normal==Vector3d.ZAxis&&c.Center.Z==0&&c.Thickness==0,Polyline p=>p.Normal==Vector3d.ZAxis&&p.Elevation==0&&p.ConstantWidth==0&&Enumerable.Range(0,p.NumberOfVertices).All(i=>p.GetStartWidthAt(i)==0&&p.GetEndWidthAt(i)==0),DBText t=>t.Normal==Vector3d.ZAxis&&t.Position.Z==0&&t.Thickness==0,MText t=>t.Normal==Vector3d.ZAxis&&t.Location.Z==0,Leader l=>l.Normal==Vector3d.ZAxis&&Enumerable.Range(0,l.NumVertices).All(i=>l.VertexAt(i).Z==0),Hatch f=>f.Normal==Vector3d.ZAxis&&f.Elevation==0,_=>false};
                    if(!planar)throw new InvalidOperationException("transform_requires_supported_zero_width_2d_wcs_entities:"+h);
                }
                if(phase2Names.Contains(h)&&entity.GetPersistentReactorIds().Count>0&&((JArray)plan["steps"]).Cast<JObject>().Any(s=>s.Value<string>("op") is "set_open_polyline" or "set_text_format" or "set_dimension_text"&&(s["targets"] as JArray??new JArray()).Cast<JObject>().Any(t=>t.Value<string>("handle")==h)))throw new InvalidOperationException("standalone_entity_without_persistent_reactors_required:"+h);
                if(FoundationDrawingPlan.Hash(before[h])!=target.Value<string>("expected_sha256"))throw new InvalidOperationException("entity_changed:"+h);
                if(entity is RasterImage&& !((JArray)plan["steps"]).Cast<JObject>().Any(s=>s.Value<string>("op")=="set_raster_path"&&((JArray)s["targets"]).Cast<JObject>().Any(t=>t.Value<string>("handle")==h)))throw new InvalidOperationException("raster_only_supports_explicit_path_repair");
                foreach(ObjectId reactor in entity.GetPersistentReactorIds())
                {if(entity is RasterImage ri&&reactor==ri.ReactorId)continue;var other=tx.GetObject(reactor,OpenMode.ForRead) as Entity;if(other==null||!names.Contains(other.Handle.ToString()))throw new InvalidOperationException("include_complete_associated_group:"+h);}
                if(entity is Leader leader&&!leader.Annotation.IsNull&&!names.Contains(leader.Annotation.Handle.ToString()))throw new InvalidOperationException("include_leader_annotation:"+h);
                if(entity is Hatch hatch)
                {
                    if(((JArray)plan["steps"]).Cast<JObject>().Any(s=>s.Value<string>("op")=="hatch"&&s["template"]?.Value<string>("handle")==h))
                    {
                        if(hatch.Associative||hatch.Normal!=Vector3d.ZAxis||hatch.Elevation!=0||hatch.GetPersistentReactorIds().Count!=0||before[h].Value<string>("loop_read_status")!="complete")throw new InvalidOperationException("standalone_complete_planar_hatch_template_required");
                        continue;
                    }
                    // A standalone legacy fill can be preserved on the hidden archive layer.
                    // Its full facts are still digest-checked and verified unchanged except layer.
                    if(archiveNames.Contains(h)&&!hatch.Associative&&hatch.Normal==Vector3d.ZAxis&&hatch.Elevation==0&&hatch.NumberOfLoops>0&&hatch.NumberOfLoops<=64&&hatch.GetPersistentReactorIds().Count==0)
                    {
                        if(before[h].Value<string>("loop_read_status")!="complete")throw new InvalidOperationException("complete_legacy_fill_geometry_required");
                        continue;
                    }
                    if(!hatch.Associative||hatch.NumberOfLoops!=1)throw new InvalidOperationException("associated_single_boundary_fill_required");
                    var ids=hatch.GetAssociatedObjectIdsAt(0);
                    if(ids.Count!=1||!names.Contains(ids[0].Handle.ToString()))throw new InvalidOperationException("include_associated_boundary");
                    var boundary=tx.GetObject(ids[0],OpenMode.ForRead);
                    if(boundary is not Circle&&!(((propertyNames.Contains(h)&&propertyNames.Contains(ids[0].Handle.ToString()))||(hatchFormatNames.Contains(h)&&hatchFormatNames.Contains(ids[0].Handle.ToString()))||(archiveNames.Contains(h)&&archiveNames.Contains(ids[0].Handle.ToString()))||(boundaryNames.Contains(h)&&boundaryNames.Contains(ids[0].Handle.ToString()))||(transformNames.Contains(h)&&transformNames.Contains(ids[0].Handle.ToString())))&&boundary is Polyline{Closed:true}))throw new InvalidOperationException("closed_surface_group_or_dot_circle_required");
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
            int observedUnits=Convert.ToInt32(doc.Database.Insunits);
            if(!FoundationDrawingPlan.UnitMetadataMatches(plan,observedUnits))throw new InvalidOperationException("drawing_unit_metadata_mismatch");
            if(((JArray)plan["steps"]).Count==1&&plan["steps"][0].Value<string>("op")=="save")return SaveDocument(doc,plan,canonicalPlan);
            if(((JArray)plan["steps"]).Count==1&&plan["steps"][0].Value<string>("op")=="plot_pdf")return PlotPdf(doc,plan,canonicalPlan);
            if(((JArray)plan["steps"]).Count==1&&plan["steps"][0].Value<string>("op")=="export_dxf")return ExportDxf(doc,plan,canonicalPlan);
            JObject before=new JObject(),after=new JObject(),objects=new JObject(),layers=new JObject(),allLayersBefore=null,allLayersAfter=null,symbolsBefore=null,symbolsAfter=null,layoutsBefore=null,layoutsAfter=null;JArray modelBefore=null,modelAfter=null;
            string activeStep=null,activeOperation=null,failureStage="preflight";var completedSteps=new JArray();
            Transaction tx=null;bool opened=false,invoked=false,committed=false;string outcome="unknown",error=null;var created=new List<string>();
            using(doc.LockDocument())try
            {
                if(doc.IsReadOnly||!doc.Database.UndoRecording)throw new InvalidOperationException("document_read_only_or_undo_disabled");
                if(!FoundationDrawingPlan.UnitMetadataMatches(plan,Convert.ToInt32(doc.Database.Insunits)))throw new InvalidOperationException("drawing_unit_metadata_changed");
                tx=doc.Database.TransactionManager.StartTransaction();modelBefore=Handles(doc.Database,tx);allLayersBefore=LayerFacts(doc.Database,tx);symbolsBefore=SymbolFacts(doc.Database,tx);layoutsBefore=LayoutFacts(doc.Database,tx);
                before=ReadEntities(doc.Database,tx,Targets(plan).Select(v=>v.Value<string>("handle")));GuardTargets(doc.Database,tx,plan,before);
                // All modifications share one transaction. Symbol/table changes and associated boundaries participate in undo.
                var db=doc.Database;var refs=new Dictionary<string,ObjectId>();ObjectId creationSpace=SymbolUtilityServices.GetBlockModelSpaceId(db);
                foreach(JObject step in (JArray)plan["steps"])
                {
                    string id=step.Value<string>("id"),op=step.Value<string>("op");activeStep=id;activeOperation=op;failureStage="executing_step";var map=new JObject();objects[id]=map;opened=true;
                    string layer=step.Value<string>("layer");int color=step.Value<int?>("color")??256;
                    ObjectId Add(Entity e,string role="entity",int? explicitColor=null)
                    {ObjectId oid=Append(db,tx,e,layer,explicitColor??color,creationSpace);map[role]=oid.Handle.ToString();created.Add(oid.Handle.ToString());return oid;}
                    switch(op)
                    {
                        case "layer":
                            string name=step.Value<string>("name");var lid=Layer(db,tx,name,step.Value<int>("color"));var lr=(LayerTableRecord)tx.GetObject(lid,OpenMode.ForRead);
                            if(lr.Color.ColorIndex!=step.Value<int>("color"))throw new InvalidOperationException("existing_layer_color_mismatch:"+name);map["layer"]=name;layers[name]=new JObject{["color_index"]=(int)lr.Color.ColorIndex,["is_off"]=lr.IsOff,["is_frozen"]=lr.IsFrozen,["is_plottable"]=lr.IsPlottable};break;
                        case "line":refs[id]=Add(new Line(Point(step["start"]),Point(step["end"])));break;
                        case "circle":refs[id]=Add(new Circle(Point(step["center"]),Vector3d.ZAxis,step.Value<double>("radius")));break;
                        case "polyline":
                            var poly=new Polyline();poly.SetDatabaseDefaults(db);var points=(JArray)step["vertices"];for(int i=0;i<points.Count;i++){var p=(JArray)points[i];poly.AddVertexAt(i,new Point2d(p[0].Value<double>(),p[1].Value<double>()),p.Count==3?p[2].Value<double>():0,0,0);}poly.Closed=step.Value<bool>("closed");if(step["constant_width"]!=null)poly.ConstantWidth=step.Value<double>("constant_width");refs[id]=Add(poly);break;
                        case "text":
                            if(!AnnotationEntityFactory.TryCreateText(TextInput(step,false),out DBText text,out string textError))throw new InvalidOperationException(textError);ApplyTextStyle(db,tx,text,step);refs[id]=Add(text);break;
                        case "mtext":
                            if(!AnnotationEntityFactory.TryCreateMText(TextInput(step,true),out MText mtext,out string mtError))throw new InvalidOperationException(mtError);ApplyTextStyle(db,tx,mtext,step);refs[id]=Add(mtext);break;
                        case "hatch":
                            if(!refs.TryGetValue(step.Value<string>("boundary"),out ObjectId boundary))throw new InvalidOperationException("boundary_reference_unavailable");
                            var curve=tx.GetObject(boundary,OpenMode.ForRead);if(curve is not Circle&&curve is not Polyline{Closed:true})throw new InvalidOperationException("closed_polyline_or_circle_required");
                            Hatch templateFill=null;if(step["template"] is JObject templateSpec){templateFill=tx.GetObject(Resolve(db,templateSpec.Value<string>("handle")),OpenMode.ForRead) as Hatch;if(templateFill==null||templateFill.PatternName!=step.Value<string>("pattern"))throw new InvalidOperationException("hatch_template_pattern_mismatch");}
                            var fill=Fill(db,tx,boundary,layer,color,step.Value<string>("pattern"),step.Value<double>("scale"),step.Value<double>("angle"),templateFill);map["entity"]=fill.Handle.ToString();created.Add(fill.Handle.ToString());refs[id]=fill.ObjectId;break;
                        case "callout":
                            if(!AnnotationEntityFactory.TryCreateMText(TextInput(step,true),out MText label,out string labelError))throw new InvalidOperationException(labelError);
                            ApplyTextStyle(db,tx,label,step);label.Attachment=step["attachment"]!=null?Enum.Parse<AttachmentPoint>(step.Value<string>("attachment")):AttachmentPoint.BottomLeft;label.Location=Point(step["position"]);ObjectId labelId=Add(label,"text",step.Value<int?>("text_color"));
                            var leader=new Leader();leader.SetDatabaseDefaults(db);leader.HasArrowHead=false;leader.IsSplined=false;leader.Dimtxt=step.Value<double>("height");leader.Dimgap=0;leader.Dimtad=0;
                            if(step["dimension_style"]!=null)leader.DimensionStyle=DimStyle(db,tx,step.Value<string>("dimension_style"));leader.HasArrowHead=false;leader.Dimtxt=step.Value<double>("height");leader.Dimgap=step.Value<double?>("gap")??0;
                            foreach(var vertex in (JArray)step["vertices"])if(!leader.AppendVertex(Point(vertex)))throw new InvalidOperationException("leader_vertex_failed");Add(leader,"leader");leader.Annotation=labelId;
                            for(int i=0;i<leader.NumVertices;i++)leader.SetVertexAt(i,Point(step["vertices"][i]));label.Location=Point(step["position"]);
                            ObjectId dot=Add(new Circle(Point(step["vertices"][0]),Vector3d.ZAxis,step.Value<double>("dot_radius")),"dot_boundary",2);
                            var dotFill=Fill(db,tx,dot,layer,2,"SOLID",1,0);map["dot_fill"]=dotFill.Handle.ToString();created.Add(dotFill.Handle.ToString());break;
                        case "set_text":
                            var existing=(Entity)tx.GetObject(Resolve(db,step.Value<string>("handle")),OpenMode.ForWrite);existing.DisableUndoRecording(false);
                            if(existing is DBText oldText)oldText.TextString=step.Value<string>("text");else if(existing is MText oldMtext)oldMtext.Contents=step.Value<string>("text");else throw new InvalidOperationException("text_entity_required");map["entity"]=existing.Handle.ToString();break;
                        case "set_polyline":
                            var oldPoly=tx.GetObject(Resolve(db,step.Value<string>("handle")),OpenMode.ForWrite) as Polyline;
                            if(oldPoly==null||!oldPoly.Closed||oldPoly.Normal!=Vector3d.ZAxis||oldPoly.Elevation!=0||oldPoly.ConstantWidth!=0||Enumerable.Range(0,oldPoly.NumberOfVertices).Any(i=>oldPoly.GetStartWidthAt(i)!=0||oldPoly.GetEndWidthAt(i)!=0))throw new InvalidOperationException("closed_zero_width_2d_wcs_boundary_required");
                            oldPoly.DisableUndoRecording(false);var pv=(JArray)step["vertices"];
                            while(oldPoly.NumberOfVertices>pv.Count)oldPoly.RemoveVertexAt(oldPoly.NumberOfVertices-1);
                            for(int i=0;i<pv.Count;i++){var v=(JArray)pv[i];var xy=new Point2d(v[0].Value<double>(),v[1].Value<double>());double bulge=v.Count==3?v[2].Value<double>():0;if(i<oldPoly.NumberOfVertices){oldPoly.SetPointAt(i,xy);oldPoly.SetBulgeAt(i,bulge);}else oldPoly.AddVertexAt(i,xy,bulge,0,0);}
                            foreach(JObject companion in (JArray)step["companions"]){var associated=tx.GetObject(Resolve(db,companion.Value<string>("handle")),OpenMode.ForWrite) as Hatch;if(associated==null)throw new InvalidOperationException("associated_hatch_required");associated.DisableUndoRecording(false);var patternType=associated.PatternType;string patternName=associated.PatternName;double scale=associated.PatternScale,angle=associated.PatternAngle;associated.RemoveLoopAt(0);associated.Associative=true;associated.AppendLoop(HatchLoopTypes.External,new ObjectIdCollection{oldPoly.ObjectId});associated.SetHatchPattern(patternType,patternName);associated.PatternScale=scale;associated.PatternAngle=angle;associated.EvaluateHatch(true);associated.RecordGraphicsModified(true);map[companion.Value<string>("handle")]=companion.Value<string>("handle");}
                            map["entity"]=oldPoly.Handle.ToString();break;
                        case "set_callout":
                            var oldLeader=tx.GetObject(Resolve(db,step.Value<string>("handle")),OpenMode.ForWrite) as Leader;var pairedText=tx.GetObject(Resolve(db,step.Value<string>("text_handle")),OpenMode.ForWrite) as MText;
                            if(oldLeader==null||pairedText==null||oldLeader.Annotation!=pairedText.ObjectId||oldLeader.NumVertices!=3||oldLeader.IsSplined)throw new InvalidOperationException("three_vertex_LEADER_and_its_MTEXT_required");
                            if(!oldLeader.HasArrowHead&&oldLeader.VertexAt(0).DistanceTo(Point(step["vertices"][0]))>0.001&&step["dot_companions"]==null)throw new InvalidOperationException("moving_separate_dot_anchor_requires_dot_companions");
                            oldLeader.DisableUndoRecording(false);pairedText.DisableUndoRecording(false);for(int i=0;i<3;i++)oldLeader.SetVertexAt(i,Point(step["vertices"][i]));pairedText.Location=Point(step["position"]);if(step["dimtad"]!=null)oldLeader.Dimtad=(short)step.Value<int>("dimtad");if(step["annotation_offset"]!=null){var off=Point(step["annotation_offset"]);oldLeader.AnnotationOffset=new Vector3d(off.X,off.Y,off.Z);}if(step["annotation_offset"]!=null||step["dimtad"]!=null)oldLeader.EvaluateLeader();if(step["has_hook_line"]!=null&&oldLeader.HasHookLine!=step.Value<bool>("has_hook_line"))throw new InvalidOperationException("native_hook_line_state_differs");map["leader"]=oldLeader.Handle.ToString();map["text"]=pairedText.Handle.ToString();
                            if(step["dot_companions"] is JArray dots)
                            {
                                var members=dots.Cast<JObject>().Select(t=>(Entity)tx.GetObject(Resolve(db,t.Value<string>("handle")),OpenMode.ForWrite)).ToArray();
                                var circle=members.OfType<Circle>().Single();var dotHatch=members.OfType<Hatch>().Single();circle.DisableUndoRecording(false);dotHatch.DisableUndoRecording(false);circle.Center=Point(step["vertices"][0]);var ids=dotHatch.GetAssociatedObjectIdsAt(0);if(ids.Count!=1||ids[0]!=circle.ObjectId)throw new InvalidOperationException("dot_fill_circle_pair_required");dotHatch.RemoveLoopAt(0);dotHatch.Associative=true;dotHatch.AppendLoop(HatchLoopTypes.External,ids);dotHatch.EvaluateHatch(true);
                                map["dot_boundary"]=circle.Handle.ToString();map["dot_fill"]=dotHatch.Handle.ToString();
                            }break;
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
                        default:if(!ExecuteExtendedStep(db,tx,step,map,refs,created,before))throw new InvalidOperationException("unsupported_plan_step");break;
                    }
                    completedSteps.Add(id);checkpoint?.Invoke("after_step:"+id);
                }
                RefreshChangedFontText(db,tx,plan);
                var observed=before.Properties().Select(p=>p.Name).Concat(created).ToArray();after=ReadEntities(db,tx,observed);modelAfter=Handles(db,tx);allLayersAfter=LayerFacts(db,tx);symbolsAfter=SymbolFacts(db,tx);layoutsAfter=LayoutFacts(db,tx);
                JObject Receipt(){var r=Build(plan,before,after,objects,layers,allLayersBefore,allLayersAfter,modelBefore,modelAfter,created,"committed_verified",true,true,null,symbolsBefore,symbolsAfter);r["layouts_before"]=layoutsBefore;r["layouts_after"]=layoutsAfter;return r;}
                failureStage="precommit_verification";activeStep=null;activeOperation=null;
                if(!FoundationDrawingPlan.VerifyReceipt(Receipt(),canonicalPlan)||!IdentitySetMatches(modelBefore,modelAfter,created))throw new InvalidOperationException("plan_precommit_verification_failed; review_supported_operation_limits_and_fresh_inputs");
                failureStage="commit";invoked=true;tx.Commit();committed=true;tx.Dispose();tx=null;checkpoint?.Invoke("after_commit");
                failureStage="postcommit_readback";using(var read=db.TransactionManager.StartTransaction()){after=ReadEntities(db,read,observed);modelAfter=Handles(db,read);allLayersAfter=LayerFacts(db,read);symbolsAfter=SymbolFacts(db,read);layoutsAfter=LayoutFacts(db,read);read.Abort();}
                outcome=FoundationDrawingPlan.VerifyReceipt(Receipt(),canonicalPlan)&&IdentitySetMatches(modelBefore,modelAfter,created)?"committed_verified":"committed_readback_failed";
            }
            catch(Exception ex)
            {
                error="stage="+failureStage+" "+(activeStep==null?"plan":"step="+activeStep+" op="+activeOperation)+": "+ex.Message;
                if(committed)outcome="committed_readback_unknown";
                else if(invoked)outcome="commit_outcome_unknown";
                else if(!opened)outcome="rejected_before_write";
                else
                {
                    try
                    {tx?.Abort();tx?.Dispose();tx=null;using var read=doc.Database.TransactionManager.StartTransaction();after=ReadEntities(doc.Database,read,before.Properties().Select(p=>p.Name));modelAfter=Handles(doc.Database,read);allLayersAfter=LayerFacts(doc.Database,read);symbolsAfter=SymbolFacts(doc.Database,read);layoutsAfter=LayoutFacts(doc.Database,read);read.Abort();outcome=JToken.DeepEquals(layoutsBefore,layoutsAfter)&&JToken.DeepEquals(before,after)&&JToken.DeepEquals(allLayersBefore,allLayersAfter)&&JToken.DeepEquals(symbolsBefore,symbolsAfter)&&JToken.DeepEquals(modelBefore,modelAfter)?"aborted_verified":"rollback_outcome_unknown";}
                    catch{outcome="rollback_outcome_unknown";}
                }
            }
            finally{tx?.Dispose();}
            var final=Build(plan,before,after,objects,layers,allLayersBefore,allLayersAfter,modelBefore,modelAfter,created,outcome,invoked,committed,error,symbolsBefore,symbolsAfter);final["failure_stage"]=error==null?JValue.CreateNull():new JValue(failureStage);final["layouts_before"]=layoutsBefore;final["layouts_after"]=layoutsAfter;final["failed_step"]=error==null?JValue.CreateNull():new JValue(activeStep);final["failed_operation"]=error==null?JValue.CreateNull():new JValue(activeOperation);final["steps_executed_before_outcome"]=completedSteps;final["step_execution_is_not_commit"]=true;return final;
        }
        static bool IdentitySetMatches(JArray before,JArray after,IEnumerable<string> created)=>before!=null&&after!=null&&after.Values<string>().ToHashSet().SetEquals(before.Values<string>().Concat(created))&&created.Distinct().Count()==created.Count();
        static JObject SaveDocument(Document doc,JObject plan,string canonicalPlan)
        {
            JObject before=new JObject(),after=new JObject(),layersBefore=null,layersAfter=null,symbolsBefore=null,symbolsAfter=null,layoutsBefore=null,layoutsAfter=null;JArray modelBefore=null,modelAfter=null;
            var step=(JObject)plan["steps"][0];var map=new JObject();var objects=new JObject{[step.Value<string>("id")]=map};bool invoked=false,returned=false;string outcome="unknown",error=null,fileSha=null;int dbmod=-1;
            using(doc.LockDocument())try
            {
                if(doc.IsReadOnly||!System.IO.File.Exists(doc.Name))throw new InvalidOperationException("save_requires_writable_existing_local_dwg");
                string[] handles=((JArray)step["expected"]).Cast<JObject>().Select(t=>t.Value<string>("handle")).ToArray();if(handles.Distinct().Count()!=handles.Length)throw new InvalidOperationException("duplicate_save_handle");
                using(var read=doc.Database.TransactionManager.StartTransaction())
                {
                    before=ReadEntities(doc.Database,read,handles);modelBefore=Handles(doc.Database,read);layersBefore=LayerFacts(doc.Database,read);symbolsBefore=SymbolFacts(doc.Database,read);layoutsBefore=LayoutFacts(doc.Database,read);
                    if(step["expected_layouts"] is JObject expectedLayouts)foreach(var row in expectedLayouts.Properties())if(layoutsBefore[row.Name]==null||FoundationDrawingPlan.Hash(layoutsBefore[row.Name])!=row.Value.Value<string>())throw new InvalidOperationException("save_layout_changed:"+row.Name);
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
                    using var read=disk.TransactionManager.StartTransaction();after=ReadEntities(disk,read,handles);modelAfter=Handles(disk,read);layersAfter=LayerFacts(disk,read);symbolsAfter=SymbolFacts(disk,read);layoutsAfter=LayoutFacts(disk,read);read.Abort();
                }
                outcome="committed_verified";
                var candidate=Build(plan,before,after,objects,new JObject(),layersBefore,layersAfter,modelBefore,modelAfter,Array.Empty<string>(),outcome,invoked,returned,null,symbolsBefore,symbolsAfter);candidate["layouts_before"]=layoutsBefore;candidate["layouts_after"]=layoutsAfter;candidate["saved_file_sha256"]=fileSha;candidate["dbmod_after_save"]=dbmod;
                if(!FoundationDrawingPlan.VerifyReceipt(candidate,canonicalPlan))outcome="committed_readback_failed";
            }
            catch(Exception ex){error=ex.Message;outcome=returned?"committed_readback_unknown":invoked?"commit_outcome_unknown":"rejected_before_write";}
            var result=Build(plan,before,after,objects,new JObject(),layersBefore,layersAfter,modelBefore,modelAfter,Array.Empty<string>(),outcome,invoked,returned,error,symbolsBefore,symbolsAfter);result["layouts_before"]=layoutsBefore;result["layouts_after"]=layoutsAfter;result["saved_file_sha256"]=fileSha==null?JValue.CreateNull():new JValue(fileSha);result["dbmod_after_save"]=dbmod;return result;
        }
        static JObject Build(JObject plan,JObject before,JObject after,JObject objects,JObject layers,JObject allLayersBefore,JObject allLayersAfter,JArray modelBefore,JArray modelAfter,IEnumerable<string> created,string outcome,bool invoked,bool committed,string error,JObject symbolsBefore=null,JObject symbolsAfter=null)=>new JObject{
            ["symbols_before"]=symbolsBefore,["symbols_after"]=symbolsAfter,
            ["verification_scope"]=((JArray)plan["steps"]).Any(s=>s.Value<string>("op")=="set_polyline")?"boundary_vertices_and_native_area_plus_associated_hatch_path;cached_hatch_area_requires_independent_read":((JArray)plan["steps"]).Any(s=>s.Value<string>("op") is "rotate" or "scale" or "copy")?"transformed_geometry_and_associations;cached_hatch_area_requires_independent_read":"declared_plan_properties",
            ["schema"]="foundation-plan-receipt-v1",["plan_sha256"]=FoundationDrawingPlan.Hash(plan),["outcome"]=outcome,["effects"]=committed?"committed":outcome=="aborted_verified"||outcome=="rejected_before_write"?"none":"unknown",["commit_invoked"]=invoked,["commit_returned"]=committed,["error"]=error==null?JValue.CreateNull():new JValue(error),["objects"]=objects,["created_handles"]=new JArray(created),["before"]=before,["after"]=after,["layers"]=layers,["all_layers_before"]=allLayersBefore,["all_layers_after"]=allLayersAfter,["model_handles_before"]=modelBefore,["model_handles_after"]=modelAfter,["journal_finalized"]=false,["fence_release_authorized"]=false};
    }
}
