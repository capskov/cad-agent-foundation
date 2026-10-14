using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution
{
 public sealed class J10WritePilotPlan
 {
  public string DrawingPath { get; }
  public string DatabaseFingerprint { get; }
  public string PairDigest { get; }
  public string Digest { get; }
  public const string NativePlanEnvironment="BIMWRIGHT_H1_J10_PLAN_FILE";
  public J10WritePilotPlan(string json)
  {
   var value=JObject.Parse(json);
   if(!value.Properties().Select(p=>p.Name).OrderBy(n=>n).SequenceEqual(new[]{"database_fingerprint","drawing_path","pair_digest","schema","source_role"})
    || value.Properties().Any(p=>p.Value.Type!=JTokenType.String)
    || value.Value<string>("schema")!="j10-isolated-write-plan-v1"||value.Value<string>("source_role")!="archived_isolated_test_only")throw new ArgumentException("Invalid isolated J10 plan.");
   string path=value.Value<string>("drawing_path");
   if(!Path.IsPathFullyQualified(path)||!path.EndsWith(".dwg",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Exact DWG path required.");
   DrawingPath=Path.GetFullPath(path);
   if(!Guid.TryParse(value.Value<string>("database_fingerprint"),out Guid guid)||guid==Guid.Empty)throw new ArgumentException("Database fingerprint required.");
   DatabaseFingerprint=guid.ToString();PairDigest=value.Value<string>("pair_digest");
   if(!Regex.IsMatch(PairDigest??"","^[A-F0-9]{64}$"))throw new ArgumentException("Frozen pair digest required.");
   Digest=Hash(new JObject{["schema"]="j10-isolated-write-plan-v1",["drawing_path"]=DrawingPath,["database_fingerprint"]=DatabaseFingerprint,["pair_digest"]=PairDigest,["source_role"]="archived_isolated_test_only"});
  }
  public static J10WritePilotPlan Load(string path)=>new J10WritePilotPlan(File.ReadAllText(path));
  internal static string Hash(JObject value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString(Formatting.None))));
 }
 public static class J10FixedHatchWriteContract
 {
  public const string ReadbackValidatorVersion="j10-fixed-curved-readback-v1";
  public static readonly string[] SourceHandles={"17A0D","17A0E"};
  static bool True(JObject o,string k)=>o[k]?.Type==JTokenType.Boolean&&o.Value<bool>(k);
  static double N(JToken value)
  {
   if(value==null||value.Type!=JTokenType.Float&&value.Type!=JTokenType.Integer)throw new ArgumentException("Numeric evidence required.");
   double n=value.Value<double>();if(!double.IsFinite(n))throw new ArgumentException("Finite evidence required.");return n;
  }
  static double[] Point(JToken o)=>new[]{N(o["x"]),N(o["y"]),N(o["z"])};
  static double Distance(double[] a,double[] b)=>Math.Sqrt(a.Zip(b,(x,y)=>(x-y)*(x-y)).Sum());
  static bool PointNear(JToken a,JToken b)=>Distance(Point(a),Point(b))<=0.001;
  static bool ZeroHeight(JToken a)=>N(a["z"])==0;
  public static bool MatchesStoredPath(JObject boundary,JObject hatch)
  {
   try
   {
    if(boundary.Value<string>("handle")!="17A0D"||boundary.Value<string>("type")!="Polyline"||!True(boundary,"is_closed")||boundary.Value<int?>("vertex_count")!=6||boundary["vertices"] is not JArray vertices||vertices.Count!=6
     ||hatch.Value<string>("handle")!="17A0E"||hatch.Value<string>("type")!="Hatch"||!True(hatch,"is_associative")||hatch.Value<int?>("loop_count")!=1||hatch.Value<string>("loop_read_status")!="complete"||hatch["loops_truncated"]?.Type!=JTokenType.Boolean||hatch.Value<bool>("loops_truncated")||hatch.Value<string>("loop_geometry_space")!="wcs"
     ||hatch["loops"] is not JArray loops||loops.Count!=1||Math.Abs(N(hatch["area"])-N(boundary["area"]))>=0.001)return false;
    var loop=(JObject)loops[0];
    if(loop.Value<string>("geometry_kind")!="curve_edges"||loop.Value<string>("geometry_status")!="available"||loop.Value<string>("association_status")!="available"||loop.Value<int?>("associated_handle_count")!=1||loop["associated_handles_truncated"]?.Type!=JTokenType.Boolean||loop.Value<bool>("associated_handles_truncated")||!JToken.DeepEquals(loop["associated_handles"],new JArray("17A0D"))
     ||loop.Value<int?>("edge_count")!=6||loop["edges_truncated"]?.Type!=JTokenType.Boolean||loop.Value<bool>("edges_truncated")||loop["edges_wcs"] is not JArray edges||edges.Count!=6)return false;
    foreach(JObject v in vertices)if(!ZeroHeight(v))return false;
    for(int start=0;start<6;start++)foreach(bool reverse in new[]{false,true})
    {
     bool match=true;
     for(int i=0;i<6&&match;i++)
     {
      var edge=(JObject)edges[(start+(reverse?6-i:i))%6];var a=vertices[i];var b=vertices[(i+1)%6];
      var edgeStart=reverse?edge["end"]:edge["start"];var edgeEnd=reverse?edge["start"]:edge["end"];
      match=ZeroHeight(edgeStart)&&ZeroHeight(edgeEnd)&&PointNear(a,edgeStart)&&PointNear(b,edgeEnd);
      double bulge=N(a["bulge"]);
      if(bulge==0)match&=edge.Value<string>("kind")=="line";
      else
      {
       if(Math.Abs(bulge)>1||edge.Value<string>("kind")!="circular_arc"||edge["is_clockwise"]?.Type!=JTokenType.Boolean){match=false;continue;}
       double[] pa=Point(a),pb=Point(b);double dx=pb[0]-pa[0],dy=pb[1]-pa[1],offset=(1-bulge*bulge)/(4*bulge);
       var center=new[]{(pa[0]+pb[0])/2-dy*offset,(pa[1]+pb[1])/2+dx*offset,0.0};double radius=Math.Sqrt(dx*dx+dy*dy)*(1+bulge*bulge)/(4*Math.Abs(bulge));
       match&=ZeroHeight(edge["center"])&&Distance(center,Point(edge["center"]))<=0.001&&Math.Abs(radius-N(edge["radius"]))<=0.001&&(reverse?!edge.Value<bool>("is_clockwise"):edge.Value<bool>("is_clockwise"))==(bulge<0);
      }
     }
     if(match)return true;
    }
   }
   catch{return false;}
   return false;
  }
  public static bool VerifyReceipt(JObject receipt,J10WritePilotPlan plan)
  {
   try
   {
    if(plan==null||receipt==null||receipt.Value<string>("schema")!="j10-native-repair-receipt-v1"||receipt.Value<string>("pilot_plan_digest")!=plan.Digest||receipt.Value<string>("boundary_handle")!="17A0D"||receipt.Value<string>("hatch_handle")!="17A0E"||receipt.Value<string>("outcome")!="committed_verified"||receipt.Value<string>("effects")!="committed"||!True(receipt,"write_opened")||!True(receipt,"commit_invoked")||!True(receipt,"commit_returned")||receipt["error"]?.Type!=JTokenType.Null)return false;
    var before=receipt["before"] as JObject;var after=receipt["after"] as JObject;
    if(before==null||after==null||J10WritePilotPlan.Hash(before)!=plan.PairDigest||!JToken.DeepEquals(before["boundary"],after["boundary"])||!MatchesStoredPath(after["boundary"] as JObject,after["hatch"] as JObject))return false;
    var a=(JObject)before["hatch"].DeepClone();var b=(JObject)after["hatch"].DeepClone();foreach(string key in new[]{"area","loops","extents"}){a.Remove(key);b.Remove(key);}return JToken.DeepEquals(a,b);
   }
   catch{return false;}
  }
  public static bool VerifyReadback(string resultJson,string readbackJson,J10WritePilotPlan plan)
  {
   try{var result=JObject.Parse(resultJson);return JToken.DeepEquals(result,JObject.Parse(readbackJson))&&VerifyReceipt(result,plan);}catch{return false;}
  }
 }
}
