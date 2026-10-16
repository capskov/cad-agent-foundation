using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Plugin
{
 internal static partial class FoundationPlanWriteHandler
 {
  static JObject ExportDxf(Document doc,JObject plan,string canonicalPlan)
  {
   var s=(JObject)plan["steps"][0];var before=new JObject();var after=new JObject();JObject layers=null,symbols=null,layouts=null,diskEntities=null;JArray model=null;var map=new JObject();bool invoked=false,published=false;string error=null,outcome="rejected_before_write",pending=null;JObject artifact=null;
   using(doc.LockDocument())try
   {
    int units=Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("INSUNITS"));if(units!=0&&units!=6)throw new InvalidOperationException("file_output_requires_metres_or_explicit_unitless_metres");
    if(doc.IsReadOnly)throw new InvalidOperationException("owned_writable_document_required");var db=doc.Database;string[] hs=((JArray)s["expected"]).Cast<JObject>().Select(t=>t.Value<string>("handle")).ToArray();
    using(var tx=db.TransactionManager.StartTransaction())
    {
     layers=LayerFacts(db,tx);symbols=SymbolFacts(db,tx);layouts=LayoutFacts(db,tx);model=Handles(db,tx);before=ReadEntities(db,tx,hs);
     foreach(JObject t in (JArray)s["expected"])if(FoundationDrawingPlan.Hash(before[t.Value<string>("handle")])!=t.Value<string>("expected_sha256"))throw new InvalidOperationException("export_entity_changed");tx.Abort();
    }
    string parent=Path.GetDirectoryName(doc.Name);if(string.IsNullOrEmpty(parent)||!File.Exists(doc.Name))throw new InvalidOperationException("saved_owned_drawing_required");string folder=Path.Combine(parent,"outputs");
    for(var d=new DirectoryInfo(folder);d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("output_reparse_path_refused");
    string output=Path.Combine(folder,s.Value<string>("file_name"));if(File.Exists(output))throw new InvalidOperationException("output_exists_no_overwrite");pending=Path.Combine(folder,"pending-"+Guid.NewGuid().ToString("N")+".dxf");
    invoked=true;Directory.CreateDirectory(folder);db.DxfOut(pending,16,DwgVersion.Current);
    using(var disk=new Database(false,true)){disk.DxfIn(pending,null);using var tx=disk.TransactionManager.StartTransaction();diskEntities=ReadEntities(disk,tx,hs);tx.Abort();}
    foreach(string h in hs)if(!FoundationDrawingPlan.Equivalent(before[h],diskEntities[h]))throw new InvalidOperationException("DXF_disk_readback_mismatch:"+h);
    using(var tx=db.TransactionManager.StartTransaction())
    {after=ReadEntities(db,tx,hs);if(!JToken.DeepEquals(before,after)||!JToken.DeepEquals(layers,LayerFacts(db,tx))||!JToken.DeepEquals(symbols,SymbolFacts(db,tx))||!JToken.DeepEquals(layouts,LayoutFacts(db,tx))||!JToken.DeepEquals(model,Handles(db,tx)))throw new InvalidOperationException("DXF_export_source_changed");tx.Abort();}
    string digest;using(var sha=System.Security.Cryptography.SHA256.Create())using(var file=File.OpenRead(pending))digest=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
    File.Move(pending,output,false);published=true;artifact=new JObject{["path"]=output,["file_name"]=s["file_name"],["sha256"]=digest,["bytes"]=new FileInfo(output).Length,["published_without_overwrite"]=true};foreach(string h in hs)map[h]=h;outcome="committed_verified";
   }
   catch(Exception ex){error="step="+s.Value<string>("id")+" op=export_dxf: "+ex.Message;outcome=published?"committed_readback_unknown":invoked?"commit_outcome_unknown":"rejected_before_write";}
   var result=Build(plan,before,after,new JObject{[s.Value<string>("id")]=map},new JObject(),layers,layers,model,model,Array.Empty<string>(),outcome,invoked,published,error,symbols,symbols);result["layouts_before"]=layouts;result["layouts_after"]=layouts;result["effects_scope"]="output_file_only";result["output_file"]=artifact;result["disk_entities"]=diskEntities;result["pending_output_path"]=pending;
   if(outcome=="committed_verified"&&!FoundationDrawingPlan.VerifyReceipt(result,canonicalPlan)){result["outcome"]="committed_readback_failed";result["error"]="DXF_receipt_verification_failed";}
   return result;
  }
 }
}
