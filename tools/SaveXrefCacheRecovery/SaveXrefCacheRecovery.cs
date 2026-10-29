using System.Reflection;
using System.Security.Cryptography;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Bimwright.Dwg.Plugin.Execution;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;
using F=Bimwright.Dwg.Shared.Execution.FoundationDrawingPlan;
[assembly:CommandClass(typeof(FoundationMaintenance.SaveXrefCacheRecovery))]
namespace FoundationMaintenance;
public class SaveXrefCacheRecovery {
 static readonly BindingFlags Flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 static void Need(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
 static string Sha(string path){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return Convert.ToHexString(SHA256.HashData(stream));}
 [CommandMethod("CFXREFREVERIFY",CommandFlags.Session)]
 public void Run(){
  var doc=Application.DocumentManager.MdiActiveDocument;
  var input=doc.Editor.GetString(new PromptStringOptions("\nFoundation recovery workspace: "){AllowSpaces=true});if(input.Status!=PromptStatus.OK)return;
  var work=Path.GetFullPath(input.StringResult.Trim('"'));string output=Path.Combine(work,"save-xref-cache-native-recovery.json");
  try {
   var session=JObject.Parse(File.ReadAllText(Path.Combine(work,"session.json")));
   Need(Environment.ProcessId==session.Value<int>("pid"),"different CAD process");
   Need(string.Equals(Path.GetFullPath(doc.Name),Path.GetFullPath(session.Value<string>("drawing")),StringComparison.OrdinalIgnoreCase),"different drawing");
   var assembly=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Bimwright.Dwg.Plugin.Acad26");
   Need(assembly.ManifestModule.ModuleVersionId.ToString()==session["installation"].Value<string>("plugin_mvid"),"different host plugin");
   var app=assembly.GetType("Bimwright.Dwg.Plugin.App",true);
   var journal=(IPluginIntentJournal)app.GetProperty("PluginJournal",Flags).GetValue(null);
   var request=JObject.Parse(File.ReadAllText(Path.Combine(work,"save-xref-cache-recovery-input.json")));
   var job=journal.GetSnapshot(request.Value<string>("request_id"));Need(job!=null,"request not found");
   Need(job.ContextId==request.Value<string>("context_id")&&job.OwnerId==journal.Owner.OwnerId&&job.PluginSequence==request.Value<long>("expected_sequence"),"request identity or sequence changed");
   Need(job.Effects==PluginEffectState.Committed&&job.Verification==PluginVerificationState.Failed&&!job.WriteFenceReleased&&!job.WriteFenceFinalized,"not a held failed committed save");
   var original=JObject.Parse(job.ResultJson);Need(F.Hash(original)==request.Value<string>("original_result_sha256"),"original receipt changed");
   var plan=F.Parse(job.CanonicalParametersJson);var steps=(JArray)plan["steps"];Need(steps.Count==1&&steps[0].Value<string>("op")=="save","only save recovery supported");
   var type=assembly.GetType("Bimwright.Dwg.Plugin.FoundationPlanWriteHandler",true);
   object Call(string name,params object[] args)=>type.GetMethod(name,Flags).Invoke(null,args);
   var handles=((JArray)steps[0]["expected"]).Select(x=>x.Value<string>("handle")).ToArray();
   using(doc.LockDocument()){
    Need(Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("DBMOD"))==0,"live drawing has unsaved changes");
    Need(Sha(doc.Name)==original.Value<string>("saved_file_sha256"),"saved file bytes changed");
    JObject Observe(Database db){using var tx=db.TransactionManager.StartTransaction();var fresh=(JObject)original.DeepClone();fresh["outcome"]="committed_verified";
     fresh["after"]=(JObject)Call("ReadEntities",db,tx,handles);fresh["model_handles_after"]=(JArray)Call("Handles",db,tx);fresh["all_layers_after"]=(JObject)Call("LayerFacts",db,tx);fresh["symbols_after"]=(JObject)Call("SymbolFacts",db,tx);fresh["layouts_after"]=(JObject)Call("LayoutFacts",db,tx);tx.Abort();return fresh;}
    var live=Observe(doc.Database);JObject diskFacts;
    using(var disk=new Database(false,true)){disk.ReadDwgFile(doc.Name,FileOpenMode.OpenForReadAndAllShare,true,null);disk.CloseInput(true);diskFacts=Observe(disk);}
    Need(Sha(doc.Name)==original.Value<string>("saved_file_sha256"),"file changed during readback");
    var proof=new JObject{["schema"]="foundation-save-xref-cache-recovery-v1",["original_result_sha256"]=F.Hash(original),["plan_sha256"]=F.Hash(plan),["native_readback"]=diskFacts,["live_readback"]=live};
    File.WriteAllText(Path.Combine(work,"save-xref-cache-recovery-proof.json"),proof.ToString());
    Need(F.VerifySaveXrefCacheRecovery(original,proof,job.CanonicalParametersJson),"fresh native proof failed; fence retained");
    var stage=journal.RecordVerificationPendingRelease(new PluginVerificationRequest(job.RequestId,job.ContextId,job.CompleteTargetJson,job.ProviderGeneration,job.PluginSequence+1,PluginVerificationState.Passed,proof.ToString(Newtonsoft.Json.Formatting.None),ownerId:job.OwnerId,payloadDigest:job.PayloadDigest,expectedPluginSequence:job.PluginSequence,committedResultDigest:job.CommittedResultDigest),DateTimeOffset.UtcNow);
    Need(stage.Disposition==PluginTransitionDisposition.Applied,"stage rejected: "+stage.Reason);
    var s=stage.Job;var final=journal.FinalizeVerifiedWriteFence(new PluginFinalizeVerifiedWriteFenceRequest(s.RequestId,s.ContextId,s.CompleteTargetJson,s.OwnerId,s.ProviderGeneration,s.PayloadDigest,s.CommittedResultDigest,s.PluginSequence,s.ReadbackRevision.Value,s.ReadbackDigest),DateTimeOffset.UtcNow);
    Need(final.Disposition==PluginTransitionDisposition.Applied&&final.Job.WriteFenceReleased,"finalization rejected: "+final.Reason);
    Need(final.Job.ResultJson==job.ResultJson,"immutable committed receipt changed");
    File.WriteAllText(output,new JObject{["passed"]=true,["request_id"]=job.RequestId,["original_result_unchanged"]=true,["save_replayed"]=false,["native_fence_released"]=true,["plugin_sequence"]=final.Job.PluginSequence}.ToString());
    doc.Editor.WriteMessage("\nFoundation original save reverified; server reconciliation required.");
   }
  } catch(System.Exception ex){File.WriteAllText(output,new JObject{["passed"]=false,["error"]=ex.ToString()}.ToString());doc.Editor.WriteMessage("\nFoundation recovery retained evidence: "+ex.Message);}
 }
}
