using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Shared.Execution
{
    // First-release drawing vocabulary, not an arbitrary AutoCAD command channel.
    public static class FoundationDrawingPlan
    {
        public const string McpToolName="dwg_apply_plan", PluginToolName="foundation_apply_plan";
        public const string Schema="foundation-drawing-plan-v1", Validator="foundation-plan-readback-v1";
        public const int MaxSteps=16, MaxHandles=64;
        public static JToken Ordered(JToken value)
        {
            if(value is JObject o)return new JObject(o.Properties().OrderBy(p=>p.Name,StringComparer.Ordinal).Select(p=>new JProperty(p.Name,Ordered(p.Value))));
            if(value is JArray a)return new JArray(a.Select(Ordered));
            return value.DeepClone();
        }
        public static string Canonical(JToken value)=>Ordered(value).ToString(Formatting.None);
        public static string Hash(JToken value)
        {using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(value)))).Replace("-","");}
        public static JObject Parse(string json)
        {
            if(json==null||json.Length>524288)throw new ArgumentException("encoded plan_json exceeds transport allowance");
            var p=JObject.Parse(json,new JsonLoadSettings{DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error});
            Keys(p,"schema","source_role","units","steps");
            if(Text(p,"units",32)!="metres")throw new ArgumentException("first release requires explicit metre drawing units");
            if(Text(p,"schema",64)!=Schema||!new[]{"annotation","existing","demolition","new_design","test"}.Contains(Text(p,"source_role",32)))throw new ArgumentException("plan schema or source_role invalid");
            if(p["steps"] is not JArray steps||steps.Count<1||steps.Count>MaxSteps)throw new ArgumentException("1..16 steps required");
            var ids=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);int vertices=0,estimatedHandles=0;
            foreach(JObject step in steps)
            {
                string id=Text(step,"id",40),op=Text(step,"op",32);
                if(!Regex.IsMatch(id,"^[A-Za-z][A-Za-z0-9_-]*$")||!ids.Add(id))throw new ArgumentException("step IDs must be unique identifiers");
                switch(op)
                {
                    case "layer":Keys(step,"id","op","name","color");Text(step,"name",255);Color(step,"color");break;
                    case "line":Keys(step,"id","op","start","end","layer","color");Point(step["start"]);Point(step["end"]);Options(step);estimatedHandles++;break;
                    case "circle":Keys(step,"id","op","center","radius","layer","color");Point(step["center"]);Positive(step,"radius");Options(step);estimatedHandles++;break;
                    case "polyline":
                        Keys(step,"id","op","vertices","closed","layer","color");Options(step);Bool(step,"closed");
                        if(step["vertices"] is not JArray points||points.Count<(step.Value<bool>("closed")?3:2)||points.Count>128)throw new ArgumentException("2..128 polyline vertices required; closed needs 3");
                        foreach(JToken v in points){Point(v,true);if(((JArray)v).Count==3&&Math.Abs(Number(v[2]))>1)throw new ArgumentException("major bulge unsupported in first release");}vertices+=points.Count;estimatedHandles++;break;
                    case "text":case "mtext":
                        if(op=="text")Keys(step,"id","op","text","position","height","rotation","layer","color");else{Keys(step,"id","op","text","position","height","rotation","width","layer","color");Positive(step,"width");}
                        Text(step,"text",2048);Point(step["position"]);Positive(step,"height");Number(step["rotation"]);Options(step);estimatedHandles++;break;
                    case "callout":
                        Keys(step,"id","op","text","position","height","width","vertices","dot_radius","layer","color");Text(step,"text",2048);Point(step["position"]);Positive(step,"height");Positive(step,"width");Positive(step,"dot_radius");Options(step);
                        if(step["vertices"] is not JArray lv||lv.Count<2||lv.Count>16)throw new ArgumentException("2..16 ordinary LEADER vertices required");foreach(JToken v in lv)Point(v);vertices+=lv.Count;estimatedHandles+=4;break;
                    case "hatch":
                        Keys(step,"id","op","boundary","pattern","scale","angle","layer","color");Options(step);Text(step,"pattern",128);Positive(step,"scale");Number(step["angle"]);
                        string reference=Text(step,"boundary",40);if(reference==id||!ids.Contains(reference))throw new ArgumentException("hatch boundary must reference an earlier step");estimatedHandles++;break;
                    case "set_text":
                        Keys(step,"id","op","handle","expected_sha256","text","companions");Expected(step);Text(step,"text",2048);
                        if(step["companions"] is not JArray companions||companions.Count>32)throw new ArgumentException("0..32 companions required");foreach(JObject t in companions){Keys(t,"handle","expected_sha256");Expected(t);}estimatedHandles+=companions.Count+1;break;
                    case "save":
                        Keys(step,"id","op","expected");if(steps.Count!=1||step["expected"] is not JArray expected||expected.Count<1||expected.Count>64)throw new ArgumentException("save is a separate whole-document operation with 1..64 expected entities");foreach(JObject t in expected){Keys(t,"handle","expected_sha256");Expected(t);}estimatedHandles+=expected.Count;break;
                    case "move":case "archive":
                        Keys(step,"id","op","targets",op=="move"?"delta":"layer");
                        if(step["targets"] is not JArray targets||targets.Count<1||targets.Count>32)throw new ArgumentException("1..32 explicit targets required");
                        var handles=new System.Collections.Generic.HashSet<string>();foreach(JObject t in targets){Keys(t,"handle","expected_sha256");Expected(t);if(!handles.Add(t.Value<string>("handle")))throw new ArgumentException("duplicate target");}
                        if(op=="move")Point(step["delta"]);else Text(step,"layer",255);estimatedHandles+=targets.Count;break;
                    default:throw new ArgumentException("unsupported first-release operation: "+op);
                }
            }
            if(vertices>1024||estimatedHandles>MaxHandles||Encoding.UTF8.GetByteCount(Canonical(p))>131072)throw new ArgumentException("plan exceeds 1024 vertices, 64 entity outcomes or semantic 128 KiB");
            return p;
        }
        public static bool IsCanonicalValid(string json)
        {try{Parse(json);return true;}catch{return false;}} // Journal normalization may escape Unicode; identity is checked by its canonical payload digest.
        public static void Keys(JObject value,params string[] names)
        {
            if(value==null||value.Properties().Count()!=names.Length||value.Properties().Any(p=>!names.Contains(p.Name,StringComparer.Ordinal)))throw new ArgumentException("unexpected or missing plan fields");
        }
        public static string Text(JObject o,string key,int maximum)
        {if(o[key]?.Type!=JTokenType.String||string.IsNullOrWhiteSpace(o.Value<string>(key))||o.Value<string>(key).Length>maximum||o.Value<string>(key).Contains('\0'))throw new ArgumentException("invalid "+key);return o.Value<string>(key);}
        public static double Number(JToken value)
        {if(value==null||value.Type!=JTokenType.Integer&&value.Type!=JTokenType.Float)throw new ArgumentException("finite number required");double n=value.Value<double>();if(double.IsNaN(n)||double.IsInfinity(n)||Math.Abs(n)>1e9)throw new ArgumentException("numeric range exceeded");return n;}
        static void Positive(JObject o,string key){if(Number(o[key])<=0)throw new ArgumentException(key+" must be positive");}
        static void Color(JObject o,string key){if(o[key]?.Type!=JTokenType.Integer||o.Value<int>(key)<1||o.Value<int>(key)>256)throw new ArgumentException("ACI 1..256 required");}
        static void Bool(JObject o,string key){if(o[key]?.Type!=JTokenType.Boolean)throw new ArgumentException("boolean required: "+key);}
        static void Options(JObject o){Text(o,"layer",255);Color(o,"color");}
        public static void Point(JToken value,bool bulge=false)
        {if(value is not JArray a||a.Count<2||a.Count>(bulge?3:2))throw new ArgumentException("2D point required");foreach(var n in a)Number(n);}
        static void Expected(JObject o)
        {if(!Regex.IsMatch(Text(o,"handle",16),"^[A-F0-9]+$")||!Regex.IsMatch(Text(o,"expected_sha256",64),"^[A-F0-9]{64}$"))throw new ArgumentException("explicit handle and native entity digest required");}
        static bool Near(JToken value,double expected,double tolerance=0.001)=>Math.Abs(Number(value)-expected)<=tolerance;
        static bool PointMatches(JToken point,JArray expected)
        {return point is JObject&&Near(point["x"],Number(expected[0]))&&Near(point["y"],Number(expected[1]))&&Near(point["z"],0);}
        static bool EntityOptions(JObject entity,JObject step)=>entity.Value<string>("layer")==step.Value<string>("layer")&&entity.Value<int?>("color_index")==step.Value<int>("color");
        static bool TextMatches(JObject entity,JObject step,bool mtext)=>entity.Value<string>("type")== (mtext?"MText":"DBText")&&entity.Value<string>("text")==step.Value<string>("text")&&PointMatches(entity[mtext?"location":"position"],(JArray)step["position"])&&Near(entity[mtext?"text_height":"height"],Number(step["height"]))&&Near(entity["rotation"],(step["rotation"]==null?0:Number(step["rotation"]))*Math.PI/180,1e-10)&&(!mtext||Near(entity["width"],Number(step["width"])));
        public static bool VerifyReceipt(JObject receipt,string canonicalPlan)
        {
            try
            {
                JObject plan=Parse(canonicalPlan);
                if(receipt.Value<string>("schema")!="foundation-plan-receipt-v1"||receipt.Value<string>("plan_sha256")!=Hash(plan)||receipt.Value<string>("outcome")!="committed_verified"||receipt.Value<string>("effects")!="committed"||receipt.Value<bool?>("commit_invoked")!=true||receipt.Value<bool?>("commit_returned")!=true||receipt["error"]?.Type!=JTokenType.Null)return false;
                if(receipt["objects"] is not JObject objects||receipt["after"] is not JObject after||receipt["before"] is not JObject before)return false;
                if(receipt["model_handles_before"] is not JArray oldModel||receipt["model_handles_after"] is not JArray newModel||receipt["created_handles"] is not JArray created||receipt["all_layers_before"] is not JObject oldLayers||receipt["all_layers_after"] is not JObject newLayers)return false;
                string[] oldIds=oldModel.Values<string>().ToArray(),newIds=newModel.Values<string>().ToArray(),newCreated=created.Values<string>().ToArray();
                if(oldIds.Length>100000||newCreated.Length>MaxHandles||oldIds.Distinct().Count()!=oldIds.Length||newIds.Distinct().Count()!=newIds.Length||newCreated.Distinct().Count()!=newCreated.Length||newCreated.Intersect(oldIds).Any()||!newIds.ToHashSet().SetEquals(oldIds.Concat(newCreated))||!after.Properties().Select(p=>p.Name).ToHashSet().SetEquals(before.Properties().Select(p=>p.Name).Concat(newCreated)))return false;
                foreach(var row in after.Properties())if(row.Value?.Value<string>("handle")!=row.Name)return false;
                foreach(var layer in oldLayers.Properties())if(!JToken.DeepEquals(layer.Value,newLayers[layer.Name]))return false;
                foreach(JObject step in (JArray)plan["steps"])
                {
                    string id=step.Value<string>("id"),op=step.Value<string>("op");
                    JObject map=objects[id] as JObject;if(map==null)return false;
                    JObject E(string role)=>after[map.Value<string>(role)??""] as JObject;
                    if(op=="layer"){if(map.Value<string>("layer")!=step.Value<string>("name")||receipt["layers"]?[step.Value<string>("name")]?.Value<int?>("color_index")!=step.Value<int>("color"))return false;continue;}
                    if(op=="save")
                    {
                        if(!Regex.IsMatch(receipt.Value<string>("saved_file_sha256")??"","^[A-F0-9]{64}$")||receipt.Value<int?>("dbmod_after_save")!=0)return false;
                        foreach(JObject t in (JArray)step["expected"]){string h=t.Value<string>("handle");if(Hash(before[h])!=t.Value<string>("expected_sha256")||!Equivalent(before[h],after[h]))return false;}continue;
                    }
                    if(op=="set_text")
                    {
                        string h=step.Value<string>("handle");if(Hash(before[h])!=step.Value<string>("expected_sha256")||after[h]?.Value<string>("text")!=step.Value<string>("text"))return false;
                        var old=(JObject)before[h].DeepClone();var next=(JObject)after[h].DeepClone();foreach(string field in new[]{"text","extents"}){old.Remove(field);next.Remove(field);}if(!JToken.DeepEquals(old,next))return false;
                        foreach(JObject c in (JArray)step["companions"]){string ch=c.Value<string>("handle");if(Hash(before[ch])!=c.Value<string>("expected_sha256")||!JToken.DeepEquals(before[ch],after[ch]))return false;}continue;
                    }
                    if(op=="move"||op=="archive")
                    {
                        foreach(JObject target in (JArray)step["targets"])
                        {
                            string h=target.Value<string>("handle");if(Hash(before[h])!=target.Value<string>("expected_sha256")||after[h] is not JObject actual)return false;
                            if(op=="archive")
                            {
                                if(actual.Value<string>("layer")!=step.Value<string>("layer"))return false;var old=(JObject)before[h].DeepClone();var next=(JObject)actual.DeepClone();old.Remove("layer");next.Remove("layer");if(!JToken.DeepEquals(old,next))return false;
                                var l=receipt["layers"]?[step.Value<string>("layer")];if(l?.Value<bool?>("is_off")!=true||l.Value<bool?>("is_frozen")!=true||l.Value<bool?>("is_plottable")!=false)return false;
                            }
                            if(op=="move"&&!Moved((JObject)before[h],actual,(JArray)step["delta"]))return false;
                        }
                        continue;
                    }
                    JObject entity=E("entity");
                    if(op!="callout"&&(entity==null||!EntityOptions(entity,step)))return false;
                    switch(op)
                    {
                        case "line":if(entity.Value<string>("type")!="Line"||!PointMatches(entity["start"],(JArray)step["start"])||!PointMatches(entity["end"],(JArray)step["end"]))return false;break;
                        case "circle":if(entity.Value<string>("type")!="Circle"||!PointMatches(entity["center"],(JArray)step["center"])||!Near(entity["radius"],Number(step["radius"])))return false;break;
                        case "polyline":
                            var vertices=(JArray)step["vertices"];if(entity.Value<string>("type")!="Polyline"||entity.Value<bool?>("is_closed")!=step.Value<bool>("closed")||entity["vertices"] is not JArray actualVertices||actualVertices.Count!=vertices.Count)return false;
                            for(int i=0;i<vertices.Count;i++){var v=(JArray)vertices[i];if(!PointMatches(actualVertices[i],v)||!Near(actualVertices[i]["bulge"],v.Count==3?Number(v[2]):0,1e-10))return false;}break;
                        case "text":case "mtext":if(!TextMatches(entity,step,op=="mtext"))return false;break;
                        case "hatch":
                            var bm=objects[step.Value<string>("boundary")] as JObject;var boundary=after[bm?.Value<string>("entity")??""] as JObject;if(!HatchMatches(entity,boundary)||entity.Value<string>("pattern_name")!=step.Value<string>("pattern")||!Near(entity["pattern_scale"],Number(step["scale"]),1e-10)||!Near(entity["pattern_angle"],Number(step["angle"]),1e-10))return false;break;
                        case "callout":
                            var text=E("text");var leader=E("leader");var dot=E("dot_boundary");var fill=E("dot_fill");
                            if(text==null||leader==null||dot==null||fill==null||!EntityOptions(text,step)||!TextMatches(text,step,true)||leader.Value<string>("type")!="Leader"||leader.Value<string>("annotation_handle")!=map.Value<string>("text")||!HatchMatches(fill,dot)||dot.Value<int?>("color_index")!=2||fill.Value<int?>("color_index")!=2||!PointMatches(dot["center"],(JArray)step["vertices"][0])||!Near(dot["radius"],Number(step["dot_radius"])))return false;
                            var lv=(JArray)step["vertices"];if(leader["vertices_wcs"] is not JArray nativeVertices||nativeVertices.Count!=lv.Count)return false;for(int i=0;i<lv.Count;i++)if(!PointMatches(nativeVertices[i],(JArray)lv[i]))return false;break;
                    }
                }
                return true;
            }
            catch{return false;}
        }
        public static bool HatchMatches(JObject hatch,JObject boundary)
        {
            if(hatch==null||boundary==null||hatch.Value<string>("type")!="Hatch"||hatch.Value<bool?>("is_associative")!=true||hatch.Value<int?>("loop_count")!=1||hatch.Value<string>("loop_read_status")!="complete"||hatch.Value<bool?>("loops_truncated")!=false||Math.Abs(Number(hatch["area"])-Number(boundary["area"]))>=0.001)return false;
            var loop=hatch["loops"]?[0] as JObject;
            if(loop==null||loop.Value<string>("association_status")!="available"||loop.Value<bool?>("associated_handles_truncated")!=false||!JToken.DeepEquals(loop["associated_handles"],new JArray(boundary.Value<string>("handle")))||loop.Value<string>("geometry_status")!="available"||hatch.Value<string>("loop_geometry_space")!="wcs")return false;
            try
            {
                if(loop.Value<string>("geometry_kind")!="curve_edges"||loop.Value<bool?>("edges_truncated")!=false||loop["edges_wcs"] is not JArray edges||loop.Value<int?>("edge_count")!=edges.Count)return false;
                if(boundary.Value<string>("type")=="Circle")
                    return edges.Count==1&&edges[0].Value<string>("kind")=="circular_arc"&&PointToPoint(edges[0]["center"],boundary["center"])&&Near(edges[0]["radius"],Number(boundary["radius"]));
                if(boundary.Value<string>("type")!="Polyline"||boundary.Value<bool?>("is_closed")!=true||boundary["vertices"] is not JArray vertices||edges.Count!=vertices.Count)return false;
                for(int start=0;start<edges.Count;start++)foreach(bool reverse in new[]{false,true})
                {
                    bool match=true;for(int i=0;i<vertices.Count&&match;i++)
                    {
                        var edge=(JObject)edges[(start+(reverse?edges.Count-i:i))%edges.Count];var a=vertices[i];var b=vertices[(i+1)%vertices.Count];
                        match=PointToPoint(a,reverse?edge["end"]:edge["start"])&&PointToPoint(b,reverse?edge["start"]:edge["end"]);
                        double bulge=Number(a["bulge"]);
                        if(bulge==0)match&=edge.Value<string>("kind")=="line";
                        else
                        {
                            if(edge.Value<string>("kind")!="circular_arc"||edge["is_clockwise"]?.Type!=JTokenType.Boolean){match=false;continue;}
                            double dx=Number(b["x"])-Number(a["x"]),dy=Number(b["y"])-Number(a["y"]),offset=(1-bulge*bulge)/(4*bulge);
                            match&=Near(edge["center"]?["x"],(Number(a["x"])+Number(b["x"]))/2-dy*offset)&&Near(edge["center"]?["y"],(Number(a["y"])+Number(b["y"]))/2+dx*offset)&&Near(edge["center"]?["z"],0)
                                &&Near(edge["radius"],Math.Sqrt(dx*dx+dy*dy)*(1+bulge*bulge)/(4*Math.Abs(bulge)))&&(reverse?!edge.Value<bool>("is_clockwise"):edge.Value<bool>("is_clockwise"))==(bulge<0);
                        }
                    }
                    if(match)return true;
                }
            }
            catch{return false;}
            return false;
        }
        static bool PointToPoint(JToken a,JToken b)=>a is JObject&&b is JObject&&Near(a["x"],Number(b["x"]))&&Near(a["y"],Number(b["y"]))&&Near(a["z"],Number(b["z"]));
        public static bool Moved(JObject before,JObject after,JArray delta)
        {
            if(before.Value<string>("type")!=after.Value<string>("type")||before.Value<string>("layer")!=after.Value<string>("layer"))return false;
            bool Match(JToken a,JToken b)=>a is JObject&&b is JObject&&Near(b["x"],Number(a["x"])+Number(delta[0]))&&Near(b["y"],Number(a["y"])+Number(delta[1]))&&Near(b["z"],Number(a["z"]));
            switch(before.Value<string>("type"))
            {
                case "Line":return Match(before["start"],after["start"])&&Match(before["end"],after["end"]);
                case "Circle":return Match(before["center"],after["center"])&&JToken.DeepEquals(before["radius"],after["radius"]);
                case "Text":case "DBText":return Match(before["position"],after["position"])&&JToken.DeepEquals(before["text"],after["text"]);
                case "MText":return Match(before["location"],after["location"])&&JToken.DeepEquals(before["text"],after["text"]);
                case "Leader":return before["vertices_wcs"] is JArray a&&after["vertices_wcs"] is JArray b&&a.Count==b.Count&&a.Zip(b,Match).All(v=>v);
                case "Hatch":return Match(before["extents"]?["min"],after["extents"]?["min"])&&Match(before["extents"]?["max"],after["extents"]?["max"])&&Math.Abs(Number(before["area"])-Number(after["area"]))<0.001;
                default:return false;
            }
        }
        public static bool VerifyReadback(string result,string readback,string plan)
        {try{var value=JObject.Parse(result);return JToken.DeepEquals(value,JObject.Parse(readback))&&VerifyReceipt(value,plan);}catch{return false;}}
        public static bool Equivalent(JToken a,JToken b,string field="")
        {
            if(a==null||b==null)return a==null&&b==null;
            if(a is JObject ao&&b is JObject bo)return ao.Properties().Count()==bo.Properties().Count()&&ao.Properties().All(p=>bo[p.Name]!=null&&Equivalent(p.Value,bo[p.Name],p.Name));
            if(a is JArray aa&&b is JArray ba)return aa.Count==ba.Count&&aa.Zip(ba,(x,y)=>Equivalent(x,y,field)).All(v=>v);
            if(a.Type is JTokenType.Float or JTokenType.Integer && b.Type is JTokenType.Float or JTokenType.Integer)
            {
                double difference=Math.Abs(Number(a)-Number(b));if(field=="area")return difference<0.001;
                return new[]{"x","y","z","radius","length","elevation","width"}.Contains(field)?difference<=0.001:difference==0;
            }
            return JToken.DeepEquals(a,b);
        }
    }
}
