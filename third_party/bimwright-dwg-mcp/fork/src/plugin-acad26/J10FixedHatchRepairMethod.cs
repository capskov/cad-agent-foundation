// Public export: historical pilot coordinates replaced by illustrative local coordinates.
// Original engineering fixtures/receipts are excluded. This is not a supported public pilot or native acceptance evidence.
using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Bimwright.Dwg.Plugin.Cad;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin
{
    // Internal engineering pilot only: no MCP or CommandMethod registration.
    // Exact document/database identity and immutable pair digest are supplied by the local pilot caller.
    internal static class J10FixedHatchRepairMethod
    {
        const string BoundaryHandle = "17A0D", HatchHandle = "17A0E";
        const double PathTolerance = 0.001, AreaTolerance = 0.001;
        static readonly double[] Coordinates = {36.54015371384,20.2996673222,37.2583939206,17.3372923364,27.4462584878,15.40804423206,26.83100679977,17.94564404036,23.2085939474,20.15429184545,38.74880151905,23.9220801745};
        static readonly double[] Bulges = {0,0,0,0.4142135623717567,0,0.41421356237577345};
        static ObjectId Id(Database db,string handle) => db.GetObjectId(false,new Handle(Convert.ToInt64(handle,16)),0);
        static JObject EntityFacts(Entity entity,Transaction tx)
        {
            var result=JObject.FromObject(CadEntityProperties.Describe(entity,tx,true));
            result["reactors"]=new JArray(entity.GetPersistentReactorIds().Cast<ObjectId>().Select(id=>id.Handle.ToString()).OrderBy(h=>h));
            return result;
        }
        internal static JObject Capture(Document doc)
        {
            using (doc.LockDocument())
            using (var tx=doc.Database.TransactionManager.StartTransaction())
            { var facts=Read(doc,tx);tx.Abort();return facts; }
        }
        static JObject Read(Document doc,Transaction tx) => new JObject {
            ["boundary"]=EntityFacts((Entity)tx.GetObject(Id(doc.Database,BoundaryHandle),OpenMode.ForRead),tx),
            ["hatch"]=EntityFacts((Entity)tx.GetObject(Id(doc.Database,HatchHandle),OpenMode.ForRead),tx)};
        internal static string Digest(JObject facts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(facts.ToString(Formatting.None))));
        static bool ApprovedBoundary(Polyline p)
        {
            if(!p.Closed||p.NumberOfVertices!=6||p.Normal!=Vector3d.ZAxis||p.Elevation!=0)return false;
            for(int i=0;i<6;i++) if(p.GetPoint2dAt(i).GetDistanceTo(new Point2d(Coordinates[2*i],Coordinates[2*i+1]))>PathTolerance||Math.Abs(p.GetBulgeAt(i)-Bulges[i])>1e-10)return false;
            return Math.Abs(p.Area-62.10373040387367)<AreaTolerance;
        }
        static bool StoredPathMatches(Polyline boundary,Hatch hatch)
        {
            if(hatch.NumberOfLoops!=1||hatch.Normal!=Vector3d.ZAxis||hatch.Elevation!=0||!hatch.Associative||Math.Abs(hatch.Area-boundary.Area)>=AreaTolerance)return false;
            var associated=hatch.GetAssociatedObjectIdsAt(0);
            if(associated.Count!=1||associated[0]!=boundary.ObjectId)return false;
            var loop=hatch.GetLoopAt(0);
            if(loop.IsPolyline||loop.Curves.Count!=6)return false;
            for(int start=0;start<6;start++)
            foreach(bool reverse in new[]{false,true})
            {
                bool match=true;
                for(int i=0;i<6&&match;i++)
                {
                    var actual=loop.Curves[(start+(reverse?6-i:i))%6];
                    Point2d a=reverse?actual.EndPoint:actual.StartPoint,b=reverse?actual.StartPoint:actual.EndPoint;
                    match=a.GetDistanceTo(boundary.GetPoint2dAt(i))<=PathTolerance&&b.GetDistanceTo(boundary.GetPoint2dAt((i+1)%6))<=PathTolerance;
                    if(boundary.GetBulgeAt(i)==0)match&=actual is LineSegment2d;
                    else if(actual is CircularArc2d arc)
                    { using var expected=boundary.GetArcSegment2dAt(i);match&=arc.Center.GetDistanceTo(expected.Center)<=PathTolerance&&Math.Abs(arc.Radius-expected.Radius)<=PathTolerance&&(reverse?!arc.IsClockWise:arc.IsClockWise)==expected.IsClockWise; }
                    else match=false;
                }
                if(match)return true;
            }
            return false;
        }
        static bool Preserved(JObject before,JObject after)
        {
            if(!JToken.DeepEquals(before["boundary"],after["boundary"]))return false;
            var a=(JObject)before["hatch"].DeepClone();var b=(JObject)after["hatch"].DeepClone();
            foreach(string field in new[]{"loops","area","extents"}) {a.Remove(field);b.Remove(field);}
            return JToken.DeepEquals(a,b);
        }
        internal static JObject Execute(Document doc,string expectedPath,string expectedFingerprint,string expectedPairDigest,Action<string> checkpoint=null)
        {
            JObject before=null,after=null;Transaction tx=null;bool opened=false,commitInvoked=false,committed=false;string outcome="unknown",error=null,phase="identity";
            using(doc.LockDocument())
            try
            {
                if(!string.Equals(Path.GetFullPath(doc.Name),Path.GetFullPath(expectedPath),StringComparison.OrdinalIgnoreCase)||!Guid.TryParse(doc.Database.FingerprintGuid.ToString(),out Guid actualFingerprint)||!Guid.TryParse(expectedFingerprint,out Guid frozenFingerprint)||actualFingerprint!=frozenFingerprint||doc.IsReadOnly||!doc.Database.UndoRecording)throw new InvalidOperationException("scope_or_undo_blocked");
                tx=doc.Database.TransactionManager.StartTransaction();before=Read(doc,tx);
                if(Digest(before)!=expectedPairDigest)throw new InvalidOperationException("pair_digest_changed");
                var boundary=tx.GetObject(Id(doc.Database,BoundaryHandle),OpenMode.ForRead) as Polyline;
                var hatch=tx.GetObject(Id(doc.Database,HatchHandle),OpenMode.ForRead) as Hatch;
                var model=SymbolUtilityServices.GetBlockModelSpaceId(doc.Database);
                if(boundary==null||hatch==null||boundary.OwnerId!=model||hatch.OwnerId!=model||!ApprovedBoundary(boundary)||hatch.NumberOfLoops!=1||!hatch.Associative||hatch.Normal!=Vector3d.ZAxis||hatch.Elevation!=0||hatch.GetAssociatedObjectIdsAt(0).Count!=1||hatch.GetAssociatedObjectIdsAt(0)[0]!=boundary.ObjectId||StoredPathMatches(boundary,hatch))throw new InvalidOperationException("fixed_pair_preflight_blocked");
                if(((LayerTableRecord)tx.GetObject(boundary.LayerId,OpenMode.ForRead)).IsLocked||((LayerTableRecord)tx.GetObject(hatch.LayerId,OpenMode.ForRead)).IsLocked)throw new InvalidOperationException("locked_layer");
                phase="repair_loop";opened=true;boundary.UpgradeOpen();hatch.UpgradeOpen();boundary.DisableUndoRecording(false);hatch.DisableUndoRecording(false);
                hatch.RemoveLoopAt(0);hatch.Associative=true;hatch.AppendLoop(HatchLoopTypes.External,new ObjectIdCollection{boundary.ObjectId});checkpoint?.Invoke("after_association");hatch.EvaluateHatch(true);
                after=Read(doc,tx);if(!StoredPathMatches(boundary,hatch)||!Preserved(before,after))throw new InvalidOperationException("precommit_verification_failed");
                phase="commit";commitInvoked=true;tx.Commit();committed=true;tx.Dispose();tx=null;checkpoint?.Invoke("after_commit");
                phase="postcommit_readback";using(var read=doc.Database.TransactionManager.StartTransaction())
                {after=Read(doc,read);boundary=(Polyline)read.GetObject(Id(doc.Database,BoundaryHandle),OpenMode.ForRead);hatch=(Hatch)read.GetObject(Id(doc.Database,HatchHandle),OpenMode.ForRead);if(!StoredPathMatches(boundary,hatch)||!Preserved(before,after))throw new InvalidOperationException("postcommit_verification_failed");read.Abort();}
                outcome="committed_verified";
            }
            catch(Exception ex)
            {
                error=ex.Message;
                if(committed)outcome="committed_readback_unknown";
                else if(commitInvoked)outcome="commit_outcome_unknown";
                else if(!opened)outcome="rejected_before_write";
                else
                {
                    try {tx?.Abort();tx?.Dispose();tx=null;after=Capture(doc);outcome=before!=null&&JToken.DeepEquals(before,after)?"aborted_verified":"rollback_outcome_unknown";}
                    catch {outcome="rollback_outcome_unknown";}
                }
            }
            finally {tx?.Dispose();}
            return new JObject { ["schema"]="j10-native-repair-receipt-v1",["boundary_handle"]=BoundaryHandle,["hatch_handle"]=HatchHandle,["outcome"]=outcome,["write_opened"]=opened,["commit_invoked"]=commitInvoked,["commit_returned"]=committed,["effects"]=committed?"committed":outcome=="aborted_verified"||outcome=="rejected_before_write"?"none":"unknown",["error"]=error==null?JValue.CreateNull():new JValue(error),["phase"]=phase,["before"]=before,["after"]=after,["journal_finalized"]=false,["fence_release_authorized"]=false,["path_tolerance_metres"]=PathTolerance,["area_tolerance_square_metres"]=AreaTolerance};
        }
    }
}
