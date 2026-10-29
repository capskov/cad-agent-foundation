using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Plugin {
 internal static partial class FoundationPlanWriteHandler {
  static string CopyFileHash(string path){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var sha=System.Security.Cryptography.SHA256.Create();return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
  static JObject SaveDocumentCopy(Document doc,JObject plan,string canonicalPlan){
   var step=(JObject)plan["steps"][0];var before=new JObject();var after=new JObject();JObject layers=null,symbols=null,layouts=null,diskEntities=null,diskLayers=null,diskSymbols=null,diskLayouts=null;JArray model=null,diskModel=null;var map=new JObject();bool invoked=false,published=false;string error=null,outcome="rejected_before_write",pending=null,name=null,nameAfter=null,sourceHash=null,sourceHashAfter=null;int dbmodBefore=-1,dbmodAfter=-1,units=-1,copyUnits=-1;JObject artifact=null;
   using(doc.LockDocument())try{
    if(doc!=Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument)throw new InvalidOperationException("copy_requires_exact_active_owned_document");
    if(doc.IsReadOnly||!File.Exists(doc.Name))throw new InvalidOperationException("saved_owned_writable_document_required");
    var db=doc.Database;name=doc.Name;if(!string.Equals(db.Filename,name,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("copy_document_database_path_mismatch");sourceHash=CopyFileHash(name);units=(int)db.Insunits;dbmodBefore=Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("DBMOD"));
    string[] handles=((JArray)step["expected"]).Cast<JObject>().Select(t=>t.Value<string>("handle")).ToArray();
    using(var tx=db.TransactionManager.StartTransaction()){
     before=ReadEntities(db,tx,handles);layers=LayerFacts(db,tx);symbols=SymbolFacts(db,tx);layouts=LayoutFacts(db,tx);model=Handles(db,tx);
     foreach(JObject target in (JArray)step["expected"])if(FoundationDrawingPlan.Hash(before[target.Value<string>("handle")])!=target.Value<string>("expected_sha256"))throw new InvalidOperationException("copy_entity_changed");tx.Abort();
    }
    // Beside the source, so existing relative xref/image paths keep their base.
    string folder=Path.GetDirectoryName(name);for(var d=new DirectoryInfo(folder);d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("copy_reparse_path_refused");
    string output=Path.Combine(folder,step.Value<string>("file_name"));if(File.Exists(output))throw new InvalidOperationException("copy_exists_no_overwrite");pending=Path.Combine(folder,"pending-copy-"+Guid.NewGuid().ToString("N")+".dwg");
    invoked=true;db.SaveAs(pending,false,DwgVersion.Current,db.SecurityParameters);
    using(var disk=new Database(false,true)){disk.ReadDwgFile(pending,FileOpenMode.OpenForReadAndAllShare,true,null);disk.CloseInput(true);copyUnits=(int)disk.Insunits;using var tx=disk.TransactionManager.StartTransaction();diskEntities=ReadEntities(disk,tx,handles);diskLayers=LayerFacts(disk,tx);diskSymbols=SymbolFacts(disk,tx);diskLayouts=LayoutFacts(disk,tx);diskModel=Handles(disk,tx);tx.Abort();}
    using(var tx=db.TransactionManager.StartTransaction()){after=ReadEntities(db,tx,handles);if(!JToken.DeepEquals(before,after)||!JToken.DeepEquals(layers,LayerFacts(db,tx))||!JToken.DeepEquals(symbols,SymbolFacts(db,tx))||!JToken.DeepEquals(layouts,LayoutFacts(db,tx))||!JToken.DeepEquals(model,Handles(db,tx)))throw new InvalidOperationException("copy_source_changed");tx.Abort();}
    nameAfter=doc.Name;if(!string.Equals(db.Filename,name,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("copy_database_path_changed");sourceHashAfter=CopyFileHash(name);dbmodAfter=Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("DBMOD"));
    foreach(string h in handles)map[h]=h;
    artifact=new JObject{["path"]=output,["file_name"]=step["file_name"],["sha256"]=CopyFileHash(pending),["bytes"]=new FileInfo(pending).Length,["published_without_overwrite"]=true};
    if(!FoundationDrawingPlan.VerifyReceipt(Receipt("committed_verified",true,null),canonicalPlan))throw new InvalidOperationException("copy_disk_or_identity_readback_mismatch");
    File.Move(pending,output,false);published=true;outcome="committed_verified";
   }catch(Exception ex){error="save_copy: "+ex.Message;outcome=published?"committed_readback_unknown":invoked?"commit_outcome_unknown":"rejected_before_write";}
   return Receipt(outcome,published,error);
   JObject Receipt(string state,bool committed,string failure){
    var r=Build(plan,before,after,new JObject{[step.Value<string>("id")]=map},new JObject(),layers,layers,model,model,Array.Empty<string>(),state,invoked,committed,failure,symbols,symbols);
    r["layouts_before"]=layouts;r["layouts_after"]=layouts;r["effects_scope"]="new_dwg_copy_only";r["copy_requires_new_explicit_binding"]=true;r["output_file"]=artifact?.DeepClone();if(r["output_file"] is JObject declared)declared["published_without_overwrite"]=committed;r["pending_output_path"]=pending;r["source_name_before"]=name;r["source_name_after"]=nameAfter;r["source_dbmod_before"]=dbmodBefore;r["source_dbmod_after"]=dbmodAfter;r["source_file_sha256_before"]=sourceHash;r["source_file_sha256_after"]=sourceHashAfter;r["source_insunits"]=units;r["copy_insunits"]=copyUnits;r["copy_disk_entities"]=diskEntities;r["copy_disk_layers"]=diskLayers;r["copy_disk_symbols"]=diskSymbols;r["copy_disk_layouts"]=diskLayouts;r["copy_disk_model_handles"]=diskModel;return r;
   }
  }
 }
}
