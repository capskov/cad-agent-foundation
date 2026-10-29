using System;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json.Linq;
public class CapabilityBatchFixture {
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
