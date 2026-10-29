using System;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json.Linq;
public class CapabilityBatchFixture {
 [CommandMethod("CAFDOCUMENTCOPYPROBE")]
 public void DocumentCopyProbe(){
  try{DocumentCopyProbeCore();}catch(System.Exception ex){File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("CAF_COPY_PROBE_FOLDER"),"error.txt"),ex.ToString());}
 }
 void DocumentCopyProbeCore(){
  string folder=Environment.GetEnvironmentVariable("CAF_COPY_PROBE_FOLDER");if(string.IsNullOrEmpty(folder)||!Directory.Exists(folder))throw new InvalidOperationException("explicit existing probe folder required");
  string output=Path.Combine(folder,"copy.dwg"),evidence=Path.Combine(folder,"result.json");if(File.Exists(output)||File.Exists(evidence))throw new InvalidOperationException("unused probe paths required");
  var doc=Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;var db=doc.Database;string name=doc.Name,filename=db.Filename;
  string Hash(string path){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));}
  string sourceHash=Hash(name),handle;
  using(doc.LockDocument()){
   using(var tx=db.TransactionManager.StartTransaction()){var model=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForWrite);var circle=new Circle(new Point3d(20,30,0),Vector3d.ZAxis,5);model.AppendEntity(circle);tx.AddNewlyCreatedDBObject(circle,true);handle=circle.Handle.ToString();tx.Commit();}
   int before=Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("DBMOD"));
   if(Environment.GetEnvironmentVariable("CAF_COPY_PROBE_FOUNDATION")=="1"){
    var assembly=System.Reflection.Assembly.LoadFrom(Environment.GetEnvironmentVariable("CAF_DIAGNOSTIC_PLUGIN"));var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
    var handler=assembly.GetType("Bimwright.Dwg.Plugin.FoundationPlanWriteHandler",true);var contract=assembly.GetType("Bimwright.Dwg.Shared.Execution.FoundationDrawingPlan",true);JObject facts;
    using(var factsRead=db.TransactionManager.StartTransaction()){facts=(JObject)handler.GetMethod("ReadEntities",flags).Invoke(null,new object[]{db,factsRead,new[]{handle}});factsRead.Abort();}
    string digest=(string)contract.GetMethod("Hash",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public).Invoke(null,new object[]{facts[handle]});
    var plan=new JObject{["schema"]="foundation-drawing-plan-v1",["source_role"]="test",["units"]="raw_drawing_units",["steps"]=new JArray(new JObject{["id"]="copy",["op"]="save_copy",["file_name"]="copy.dwg",["expected"]=new JArray(new JObject{["handle"]=handle,["expected_sha256"]=digest})})};
    var receipt=(JObject)handler.GetMethod("ExecutePlan",flags).Invoke(null,new object[]{doc,plan.ToString(),null});File.WriteAllText(Path.Combine(folder,"foundation-receipt.json"),receipt.ToString());
    if(receipt.Value<string>("outcome")!="committed_verified")throw new InvalidOperationException("save-copy plan did not verify; preserve pending artifact and receipt");
   }else db.SaveAs(output,false,DwgVersion.Current,db.SecurityParameters);
   int after=Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("DBMOD"));
   using var disk=new Database(false,true);disk.ReadDwgFile(output,FileOpenMode.OpenForReadAndAllShare,true,null);disk.CloseInput(true);using var read=disk.TransactionManager.StartTransaction();var circleOnDisk=read.GetObject(disk.GetObjectId(false,new Handle(Convert.ToInt64(handle,16)),0),OpenMode.ForRead) as Circle;bool copied=circleOnDisk!=null&&circleOnDisk.Radius==5;read.Abort();
   bool identity=doc.Name==name&&db.Filename==filename,sourceUnchanged=Hash(name)==sourceHash;
   File.WriteAllText(evidence,new JObject{["dbmod_before"]=before,["dbmod_after"]=after,["identity_stable"]=identity,["source_file_unchanged"]=sourceUnchanged,["unsaved_circle_in_copy"]=copied,["handle"]=handle}.ToString());
   if(!identity||!sourceUnchanged||!copied||before!=after)throw new InvalidOperationException("document save-copy invariants differ; inspect evidence");
  }
 }
 [CommandMethod("CAFWHOLECOPYPROBE")]
 public void WholeCopyProbe(){
  string folder=Environment.GetEnvironmentVariable("CAF_COPY_PROBE_FOLDER");if(string.IsNullOrEmpty(folder)||!Directory.Exists(folder))throw new InvalidOperationException("explicit existing probe folder required");
  string source=Path.Combine(folder,"source.dwg"),copy=Path.Combine(folder,"copy.dwg"),evidence=Path.Combine(folder,"result.json");if(File.Exists(source)||File.Exists(copy)||File.Exists(evidence))throw new InvalidOperationException("unused probe paths required");
  using var db=new Database(true,true);db.Insunits=UnitsValue.Millimeters;
  using(var tx=db.TransactionManager.StartTransaction()){
   foreach(DBDictionaryEntry entry in (DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead)){var layout=(Layout)tx.GetObject(entry.Value,OpenMode.ForRead);if(layout.ModelType)continue;var paper=(BlockTableRecord)tx.GetObject(layout.BlockTableRecordId,OpenMode.ForWrite);var text=new DBText{TextString="COPY PAPER",Position=new Point3d(10,20,0),Height=3};paper.AppendEntity(text);tx.AddNewlyCreatedDBObject(text,true);break;}tx.Commit();
  }db.SaveAs(source,DwgVersion.Current);string sourceName=db.Filename;string handle;
  using(var tx=db.TransactionManager.StartTransaction()){var model=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForWrite);var circle=new Circle(new Point3d(20,30,0),Vector3d.ZAxis,5);model.AppendEntity(circle);tx.AddNewlyCreatedDBObject(circle,true);handle=circle.Handle.ToString();tx.Commit();}
  if(Environment.GetEnvironmentVariable("CAF_COPY_PROBE_DIRECT")=="1")db.SaveAs(copy,false,DwgVersion.Current,db.SecurityParameters);else using(var cloned=db.Wblock()){cloned.SaveAs(copy,DwgVersion.Current);}
  JObject Summary(Database data){using var tx=data.TransactionManager.StartTransaction();var spaces=new JObject();foreach(DBDictionaryEntry entry in (DBDictionary)tx.GetObject(data.LayoutDictionaryId,OpenMode.ForRead)){var page=(Layout)tx.GetObject(entry.Value,OpenMode.ForRead);var rows=new JArray();foreach(ObjectId id in (BlockTableRecord)tx.GetObject(page.BlockTableRecordId,OpenMode.ForRead)){var entity=(Entity)tx.GetObject(id,OpenMode.ForRead);rows.Add(new JObject{["handle"]=id.Handle.ToString(),["type"]=entity.GetType().Name});}spaces[page.LayoutName]=rows;}tx.Abort();return new JObject{["spaces"]=spaces,["insunits"]=(int)data.Insunits};}
  using var disk=new Database(false,true);disk.ReadDwgFile(copy,FileOpenMode.OpenForReadAndAllShare,true,null);disk.CloseInput(true);
  var a=Summary(db);var b=Summary(disk);bool same=JToken.DeepEquals(a,b),nameStable=db.Filename==sourceName;
  File.WriteAllText(evidence,new JObject{["same_handles_types_layouts_units"]=same,["source_name_stable"]=nameStable,["unsaved_circle_handle"]=handle,["source"]=a,["copy"]=b}.ToString());
  if(!same||!nameStable)throw new InvalidOperationException("whole copy identity or contents differ; inspect evidence");
 }
 [CommandMethod("CAFLAYERSTYLECHECK")]
 public void LayerStyleCheck(){
  string plugin=Environment.GetEnvironmentVariable("CAF_DIAGNOSTIC_PLUGIN"),output=Environment.GetEnvironmentVariable("CAF_DIAGNOSTIC_JSON");
  if(string.IsNullOrEmpty(plugin)||!File.Exists(plugin)||string.IsNullOrEmpty(output)||File.Exists(output))throw new InvalidOperationException("explicit plugin and unused evidence path required");
  var assembly=System.Reflection.Assembly.LoadFrom(plugin);var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
  var handler=assembly.GetType("Bimwright.Dwg.Plugin.FoundationPlanWriteHandler",true);var contract=assembly.GetType("Bimwright.Dwg.Shared.Execution.FoundationDrawingPlan",true);
  using var db=new Database(true,true);
  using(var tx=db.TransactionManager.StartTransaction()){
   var layers=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForWrite);var layer=new LayerTableRecord{Name="LAYER-STYLE-TEST"};layers.Add(layer);tx.AddNewlyCreatedDBObject(layer,true);
   var kinds=(LinetypeTable)tx.GetObject(db.LinetypeTableId,OpenMode.ForWrite);var kind=new LinetypeTableRecord{Name="TEST-DASH",NumDashes=2,PatternLength=10};kind.SetDashLengthAt(0,5);kind.SetDashLengthAt(1,-5);kinds.Add(kind);tx.AddNewlyCreatedDBObject(kind,true);tx.Commit();
  }
  JObject before,after,step;
  using(var tx=db.TransactionManager.StartTransaction()){
   before=(JObject)handler.GetMethod("LayerFacts",flags).Invoke(null,new object[]{db,tx});
   string digest=(string)contract.GetMethod("Hash",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{before["LAYER-STYLE-TEST"]});
   step=new JObject{["id"]="style",["op"]="set_layer",["name"]="LAYER-STYLE-TEST",["expected_sha256"]=digest,["color"]=2,["is_off"]=false,["is_frozen"]=false,["is_locked"]=false,["is_plottable"]=true,["lineweight"]=15,["linetype"]="TEST-DASH"};
   handler.GetMethod("ExecuteExtendedStep",flags).Invoke(null,new object[]{db,tx,step,new JObject(),new System.Collections.Generic.Dictionary<string,ObjectId>(),new System.Collections.Generic.List<string>(),new JObject()});tx.Commit();
  }
  using(var tx=db.TransactionManager.StartTransaction()){after=(JObject)handler.GetMethod("LayerFacts",flags).Invoke(null,new object[]{db,tx});tx.Abort();}
  bool verified=(bool)contract.GetMethod("VerifyLayerChanges",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{before,after,new JArray(step)});
  if(!verified)throw new InvalidOperationException("layer plotting properties readback mismatch: "+after);
  File.WriteAllText(output,new JObject{["passed"]=true,["scope"]="native_primitive_transient_database_no_project_or_journal_write",["before"]=before,["after"]=after,["step"]=step}.ToString());
 }
 [CommandMethod("CAFVIEWPORTPLACEMENTCHECK")]
 public void ViewportPlacementCheck(){
  string plugin=Environment.GetEnvironmentVariable("CAF_DIAGNOSTIC_PLUGIN"),output=Environment.GetEnvironmentVariable("CAF_DIAGNOSTIC_JSON");
  if(string.IsNullOrEmpty(plugin)||!File.Exists(plugin)||string.IsNullOrEmpty(output)||File.Exists(output))throw new InvalidOperationException("explicit plugin and unused evidence path required");
  var assembly=System.Reflection.Assembly.LoadFrom(plugin);var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
  var handler=assembly.GetType("Bimwright.Dwg.Plugin.FoundationPlanWriteHandler",true);var contract=assembly.GetType("Bimwright.Dwg.Shared.Execution.FoundationDrawingPlan",true);
  using var db=new Database(true,true);string name=null,handle=null;
  using(var tx=db.TransactionManager.StartTransaction()){
   foreach(DBDictionaryEntry entry in (DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead)){
    var page=(Layout)tx.GetObject(entry.Value,OpenMode.ForRead);if(page.ModelType)continue;name=page.LayoutName;
    var paper=(BlockTableRecord)tx.GetObject(page.BlockTableRecordId,OpenMode.ForWrite);
    if(page.GetViewports().Count==0){var overall=new Viewport();paper.AppendEntity(overall);tx.AddNewlyCreatedDBObject(overall,true);}
    var vp=new Viewport{CenterPoint=new Point3d(100,80,0),Width=120,Height=60,ViewDirection=Vector3d.ZAxis};paper.AppendEntity(vp);tx.AddNewlyCreatedDBObject(vp,true);vp.CustomScale=2;vp.ViewCenter=new Point2d(10,20);vp.Locked=true;handle=vp.Handle.ToString();break;
   }tx.Commit();
  }
  JObject before,after,step;
  using(var tx=db.TransactionManager.StartTransaction()){
   before=(JObject)handler.GetMethod("PaperViewportFacts",flags).Invoke(null,new object[]{db,tx});
   var original=before[handle]??throw new InvalidOperationException("test floating viewport missing");
   string digest=(string)contract.GetMethod("Hash",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{original});
   step=new JObject{["id"]="vp",["op"]="set_paper_viewport",["viewport_handle"]=handle,["expected_sha256"]=digest,["layout"]=name,["center_paper"]=new JArray(200,180),["width_paper"]=140,["view_center"]=new JArray(10,20),["view_height"]=30,["locked"]=true};
   handler.GetMethod("SetPaperViewport",flags).Invoke(null,new object[]{db,tx,step,new JObject()});tx.Commit();
  }
  using(var tx=db.TransactionManager.StartTransaction()){after=(JObject)handler.GetMethod("PaperViewportFacts",flags).Invoke(null,new object[]{db,tx});tx.Abort();}
  bool verified=(bool)contract.GetMethod("VerifyPaperViewportChanges",flags).Invoke(null,new object[]{before,after,new JArray(step)});
  if(!verified)throw new InvalidOperationException("native viewport placement readback mismatch: "+after);
  File.WriteAllText(output,new JObject{["passed"]=true,["scope"]="native_primitive_transient_database_no_project_or_journal_write",["before"]=before,["after"]=after,["step"]=step}.ToString());
 }
 [CommandMethod("CAFQUERYLAYOUTCHECK")]
 public void QueryLayoutCheck(){
  string plugin=Environment.GetEnvironmentVariable("CAF_DIAGNOSTIC_PLUGIN"),output=Environment.GetEnvironmentVariable("CAF_DIAGNOSTIC_JSON");
  if(string.IsNullOrEmpty(plugin)||!File.Exists(plugin)||string.IsNullOrEmpty(output)||File.Exists(output))throw new InvalidOperationException("explicit plugin and unused evidence path required");
  var assembly=System.Reflection.Assembly.LoadFrom(plugin);var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
  var optionType=assembly.GetType("Bimwright.Dwg.Plugin.Cad.CadEntityQueryOptions",true);var service=assembly.GetType("Bimwright.Dwg.Plugin.Cad.CadQueryService",true);
  using var db=new Database(true,true);string name=null;
  using(var tx=db.TransactionManager.StartTransaction()){
   var dictionary=(DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);
   foreach(DBDictionaryEntry entry in dictionary){var layout=(Layout)tx.GetObject(entry.Value,OpenMode.ForRead);if(layout.ModelType)continue;name=layout.LayoutName;var space=(BlockTableRecord)tx.GetObject(layout.BlockTableRecordId,OpenMode.ForWrite);for(int i=0;i<3;i++){var text=new DBText{TextString="PAPER "+i,Position=new Point3d(20,20+10*i,0),Height=3};space.AppendEntity(text);tx.AddNewlyCreatedDBObject(text,true);}break;}
   tx.Commit();
  }
  object Query(string layout,int offset){var options=Activator.CreateInstance(optionType,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,new object[]{"DBText",null,null,2,false,null,offset,layout},null);return service.GetMethod("QueryEntities",flags).Invoke(null,new[]{(object)db,options});}
  int Count(object result)=>(int)result.GetType().GetProperty("Entities",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(result).GetType().GetProperty("Count").GetValue(result.GetType().GetProperty("Entities",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(result));
  var first=Query(name,0);var second=Query(name,2);int modelCount=Count(Query(null,0)),firstCount=Count(first),secondCount=Count(second);
  bool more=(bool)first.GetType().GetProperty("HasMore",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(first);
  if(modelCount!=0||firstCount!=2||secondCount!=1||!more)throw new InvalidOperationException("layout query scope/pagination failure");
  object Options(string layout)=>Activator.CreateInstance(optionType,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,new object[]{"DBText",null,null,2,false,null,0,layout},null);
  int totalPaper=(int)service.GetMethod("CountEntities",flags).Invoke(null,new[]{(object)db,Options(name)});
  int totalModel=(int)service.GetMethod("CountEntities",flags).Invoke(null,new[]{(object)db,Options(null)});
  var selected=(System.Collections.ICollection)service.GetMethod("QueryHandles",flags).Invoke(null,new[]{(object)db,Options(name)});
  var modelSelected=(System.Collections.ICollection)service.GetMethod("QueryHandles",flags).Invoke(null,new[]{(object)db,Options(null)});
  bool missingRejected=false;try{Query("MISSING-LAYOUT",0);}catch(System.Reflection.TargetInvocationException ex){missingRejected=ex.InnerException is InvalidOperationException&&ex.InnerException.Message=="query_layout_missing:MISSING-LAYOUT";}
  if(totalPaper!=3||totalModel!=0||selected.Count!=2||modelSelected.Count!=0||!missingRejected)throw new InvalidOperationException("layout count/handle query failure");
  File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(new{layout=name,modelCount,firstCount,secondCount,more,totalPaper,totalModel,selected=selected.Count,modelSelected=modelSelected.Count,missingRejected,passed=true,scope="native_service_transient_fixture_no_project_write"}));
 }
 [CommandMethod("CAFREADDXFEVIDENCE")]
 public void ReadDxfEvidence(){
  string input=Environment.GetEnvironmentVariable("CAF_DIAGNOSTIC_DXF");
  string output=Environment.GetEnvironmentVariable("CAF_DIAGNOSTIC_JSON");
  if(string.IsNullOrEmpty(input)||!File.Exists(input)||string.IsNullOrEmpty(output)||File.Exists(output))throw new InvalidOperationException("explicit input and unused evidence path required");
  using var db=new Database(false,true);db.DxfIn(input,null);
  using var tx=db.TransactionManager.StartTransaction();var rows=new System.Collections.Generic.List<object>();
  foreach(string handle in new[]{"85","86","87"}){
   var entity=(Entity)tx.GetObject(db.GetObjectId(false,new Handle(Convert.ToInt64(handle,16)),0),OpenMode.ForRead);
   object geometry=entity is Arc a?new{a.StartAngle,a.EndAngle,a.TotalAngle,a.Radius,a.Length}:entity is Ellipse e?new{e.StartAngle,e.EndAngle,e.StartParam,e.EndParam,e.RadiusRatio,MajorAxis=new[]{e.MajorAxis.X,e.MajorAxis.Y},e.Area}:entity is Circle c?new{c.Radius,c.Area}:null;
   rows.Add(new{handle,type=entity.GetType().Name,geometry});
  }
  tx.Abort();File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(rows));
 }
 [CommandMethod("CAFCURVEOBSERVE")]
 public void Observe(){
  string folder=Environment.GetEnvironmentVariable("CAF_BATCH_FIXTURE_DIR");if(string.IsNullOrEmpty(folder)||!Directory.Exists(folder))throw new InvalidOperationException("explicit fixture folder required");
  string path=Path.Combine(folder,"curve-observation.json");if(File.Exists(path))throw new InvalidOperationException("observation exists");
  using var arc=new Arc(new Point3d(120,140,0),Vector3d.ZAxis,40,20*Math.PI/180,150*Math.PI/180);
  double start=300*Math.PI/180;arc.Radius=55;arc.StartAngle=start;arc.EndAngle=start+120*Math.PI/180;
  using var ellipse=new Ellipse(new Point3d(260,140,0),Vector3d.ZAxis,new Vector3d(60,0,0),.5,0,2*Math.PI);
  ellipse.Set(new Point3d(275,150,0),Vector3d.ZAxis,new Vector3d(45,25,0),.65,0,2*Math.PI);
  File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new {arc=new {arc.StartAngle,arc.EndAngle,length=arc.GetDistanceAtParameter(arc.EndParam)-arc.GetDistanceAtParameter(arc.StartParam)},ellipse=new {ellipse.StartAngle,ellipse.EndAngle,ellipse.StartParam,ellipse.EndParam,ellipse.RadiusRatio,major=new[]{ellipse.MajorAxis.X,ellipse.MajorAxis.Y}}}));
 }
 [CommandMethod("CAFCAPABILITYFIXTURE")]
 public void Create(){
  string folder=Environment.GetEnvironmentVariable("CAF_BATCH_FIXTURE_DIR");
  if(string.IsNullOrEmpty(folder)||!Directory.Exists(folder))throw new InvalidOperationException("explicit existing fixture folder required");
  string path=Path.Combine(folder,"source-mm.dwg");if(File.Exists(path))throw new InvalidOperationException("fixture exists; no overwrite");
  using var db=new Database(true,true);db.Insunits=UnitsValue.Millimeters;
  using(var tx=db.TransactionManager.StartTransaction()){
   var model=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForWrite);
   foreach(double x in new[]{0.0,420.0}){
    var frame=new Polyline();frame.AddVertexAt(0,new Point2d(x,0),0,0,0);frame.AddVertexAt(1,new Point2d(x+420,0),0,0,0);frame.AddVertexAt(2,new Point2d(x+420,297),0,0,0);frame.AddVertexAt(3,new Point2d(x,297),0,0,0);frame.Closed=true;model.AppendEntity(frame);tx.AddNewlyCreatedDBObject(frame,true);
   }
   var layouts=(DBDictionary)tx.GetObject(db.LayoutDictionaryId,OpenMode.ForRead);
   foreach(DBDictionaryEntry item in layouts){var page=(Layout)tx.GetObject(item.Value,OpenMode.ForRead);if(page.ModelType)continue;var paper=(BlockTableRecord)tx.GetObject(page.BlockTableRecordId,OpenMode.ForWrite);for(int i=0;i<3;i++){var label=new DBText{TextString="PAPER "+i,Position=new Point3d(20,20+i*10,0),Height=3};paper.AppendEntity(label);tx.AddNewlyCreatedDBObject(label,true);}break;}
   tx.Commit();
  }db.SaveAs(path,DwgVersion.Current);
 }
}
