using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Shared.Execution
{
 public static partial class FoundationDrawingPlan
 {
  static bool ValidateDxf(JObject s,ref int count)
  {
   if(s.Value<string>("op")!="export_dxf")return false;
   Keys(s,"id","op","file_name","expected");
   if(!Regex.IsMatch(Text(s,"file_name",100),"^[A-Za-z0-9][A-Za-z0-9_-]{0,79}\\.dxf\\z"))throw new ArgumentException("simple ASCII DXF filename required");
   if(s["expected"] is not JArray a||a.Count<1||a.Count>64)throw new ArgumentException("1..64 disk-verified entities required");foreach(JObject t in a){Keys(t,"handle","expected_sha256");Expected(t);}if(a.Cast<JObject>().Select(t=>t.Value<string>("handle")).Distinct().Count()!=a.Count)throw new ArgumentException("duplicate expected DXF entity");count+=a.Count;return true;
  }
  static bool VerifyDxf(JObject s,JObject receipt)
  {
   var file=receipt["output_file"] as JObject;var map=receipt["objects"]?[s.Value<string>("id")] as JObject;
   if(file==null||map==null||file.Value<string>("file_name")!=s.Value<string>("file_name")||!Regex.IsMatch(file.Value<string>("sha256")??"","^[A-F0-9]{64}$")||file.Value<bool?>("published_without_overwrite")!=true||file.Value<long?>("bytes")<=5||receipt.Value<string>("effects_scope")!="output_file_only"||receipt["disk_entities"] is not JObject disk)return false;
   foreach(JObject t in (JArray)s["expected"])
   {string h=t.Value<string>("handle");if(map.Value<string>(h)!=h||Hash(receipt["before"][h])!=t.Value<string>("expected_sha256")||!Equivalent(receipt["before"][h],receipt["after"][h])||!Equivalent(receipt["before"][h],disk[h]))return false;}
   return true;
  }
 }
}
