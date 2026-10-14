using Newtonsoft.Json.Linq;
using Bimwright.Dwg.Shared.Execution;

if(args.Length==2&&args[0]=="--diagnose")
{
 var payload=JObject.Parse(File.ReadAllText(args[1]));var receipt=(JObject)payload["receipt"];var plan=(JObject)payload["plan"];
 Console.WriteLine("whole="+FoundationDrawingPlan.VerifyReceipt(receipt,plan.ToString()));
 Console.WriteLine("hash_match="+(receipt.Value<string>("plan_sha256")==FoundationDrawingPlan.Hash(plan)));
 Console.WriteLine("symbols="+FoundationDrawingPlan.VerifySymbols(receipt,(JArray)plan["steps"]));
 Console.WriteLine("layers="+FoundationDrawingPlan.VerifyLayerChanges((JObject)receipt["all_layers_before"],(JObject)receipt["all_layers_after"],(JArray)plan["steps"]));
 foreach(JObject step in (JArray)plan["steps"]){bool ok=FoundationDrawingPlan.VerifyExtension(step,receipt,out bool handled);Console.WriteLine(step.Value<string>("id")+" "+step.Value<string>("op")+" handled="+handled+" ok="+ok);}
 foreach(string table in new[]{"text_styles","dimension_styles"})foreach(var row in ((JObject)receipt["symbols_before"][table]).Properties())if(!JToken.DeepEquals(row.Value,receipt["symbols_after"][table][row.Name]))Console.WriteLine("Changed old symbol "+table+"/"+row.Name);
 return;
}

int passed=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;}
JObject Point(double x,double y)=>new(){["x"]=x,["y"]=y,["z"]=0};
JObject Plan(JObject step)=>new(){["schema"]=FoundationDrawingPlan.Schema,["source_role"]="test",["units"]="metres",["steps"]=new JArray(step)};
bool Valid(JObject step){try{FoundationDrawingPlan.Parse(Plan(step).ToString());return true;}catch{return false;}}
JObject Receipt(JObject step,JObject before,JObject after,JObject map,JObject layers=null)=>new(){["schema"]="foundation-plan-receipt-v1",["plan_sha256"]=FoundationDrawingPlan.Hash(Plan(step)),["outcome"]="committed_verified",["effects"]="committed",["commit_invoked"]=true,["commit_returned"]=true,["error"]=JValue.CreateNull(),["objects"]=new JObject{[step.Value<string>("id")]=map},["before"]=before,["after"]=after,["all_layers_before"]=layers??new JObject(),["all_layers_after"]=layers?.DeepClone()??new JObject(),["model_handles_before"]=new JArray(before.Properties().Select(p=>p.Name)),["model_handles_after"]=new JArray(after.Properties().Select(p=>p.Name)),["created_handles"]=new JArray(after.Properties().Select(p=>p.Name).Except(before.Properties().Select(p=>p.Name))),["layers"]=new JObject()};
bool Verify(JObject step,JObject receipt)=>FoundationDrawingPlan.VerifyReceipt(receipt,Plan(step).ToString());

var circle=new JObject{["handle"]="A",["type"]="Circle",["layer"]="T",["color_index"]=2,["center"]=Point(3,4),["radius"]=2,["diameter"]=4,["area"]=4*Math.PI,["circumference"]=4*Math.PI};
var targets=new JArray(new JObject{["handle"]="A",["expected_sha256"]=FoundationDrawingPlan.Hash(circle)});
var scale=new JObject{["id"]="s",["op"]="scale",["targets"]=targets,["base"]=new JArray(0,0),["factor"]=2};
Check(Valid(scale),"valid scale schema");
var scaled=FoundationDrawingPlan.TransformFacts(circle,scale);var rec=Receipt(scale,new JObject{["A"]=circle},new JObject{["A"]=scaled},new JObject{["A"]="A"});
Check(Verify(scale,rec),"scale native-facts contract");
var corrupt=(JObject)rec.DeepClone();corrupt["after"]["A"]["radius"]=3;Check(!Verify(scale,corrupt),"tampered radius rejected");
corrupt=(JObject)rec.DeepClone();corrupt["after"]["A"]["color_index"]=3;Check(!Verify(scale,corrupt),"unplanned color rejected");
var invalid=(JObject)scale.DeepClone();invalid["factor"]=-1;Check(!Valid(invalid),"negative/reflection scale rejected");
invalid=(JObject)scale.DeepClone();invalid["targets"]=new JArray(targets[0].DeepClone(),targets[0].DeepClone());Check(!Valid(invalid),"duplicate target rejected");
var rotate=(JObject)scale.DeepClone();rotate["op"]="rotate";rotate.Remove("factor");rotate["angle"]=90;
var rotated=FoundationDrawingPlan.TransformFacts(circle,rotate);Check(Math.Abs(rotated["center"].Value<double>("x")+4)<1e-9&&Math.Abs(rotated["center"].Value<double>("y")-3)<1e-9,"rotation geometry");
var copy=new JObject{["id"]="c",["op"]="copy",["targets"]=targets.DeepClone(),["delta"]=new JArray(5,-2)};
var copied=FoundationDrawingPlan.TransformFacts(circle,copy);copied["handle"]="B";var cr=Receipt(copy,new JObject{["A"]=circle.DeepClone()},new JObject{["A"]=circle.DeepClone(),["B"]=copied},new JObject{["A"]="B"});
Check(Verify(copy,cr),"copy source and exact new identity");
corrupt=(JObject)cr.DeepClone();corrupt["after"]["A"]["radius"]=9;Check(!Verify(copy,corrupt),"copy source mutation rejected");
var font=new JObject{["id"]="f",["op"]="text_style",["name"]="Test",["font_file"]="txt.shx",["big_font_file"]="",["width_factor"]=1};Check(Valid(font),"installed font basename schema");font["font_file"]="C:\\private\\font.ttf";Check(!Valid(font),"arbitrary font path rejected");
var oldLayer=new JObject{["handle"]="C",["color_index"]=7,["is_off"]=false,["is_frozen"]=false,["is_locked"]=false,["is_plottable"]=true,["linetype_handle"]="D"};
var layer=new JObject{["id"]="l",["op"]="set_layer",["name"]="T",["expected_sha256"]=FoundationDrawingPlan.Hash(oldLayer),["color"]=2,["is_off"]=true,["is_frozen"]=false,["is_locked"]=true,["is_plottable"]=false};
var newLayer=(JObject)oldLayer.DeepClone();newLayer["color_index"]=2;newLayer["is_off"]=true;newLayer["is_locked"]=true;newLayer["is_plottable"]=false;
var lr=Receipt(layer,new JObject(),new JObject(),new JObject{["layer"]="T"},new JObject{["T"]=oldLayer});lr["all_layers_after"]["T"]=newLayer;
Check(Verify(layer,lr),"explicit expected layer state");
corrupt=(JObject)lr.DeepClone();corrupt["all_layers_after"]["T"]["linetype_handle"]="F";Check(!Verify(layer,corrupt),"unplanned linetype rejected");
var dimension=new JObject{["id"]="d",["op"]="aligned_dimension",["start"]=new JArray(0,0),["end"]=new JArray(3,4),["position"]=new JArray(4,0),["dimension_style"]="D",["layer"]="T",["color"]=2};
var style=new JObject{["text_height"]=1,["arrow_size"]=0.2,["overall_scale"]=1,["gap"]=0.1,["decimal_places"]=2,["dimension_color"]=2,["extension_color"]=2,["text_color"]=7};
var e=new JObject{["handle"]="A",["type"]="AlignedDimension",["dimension_kind"]="aligned_dimension",["dimension_style"]="D",["dimension_text"]="",["layer"]="T",["color_index"]=2,["start"]=Point(0,0),["end"]=Point(3,4),["dimension_line_point"]=Point(4,0),["measurement"]=5};
foreach(var f in style.Properties())e[f.Name]=f.Value.DeepClone();var dr=Receipt(dimension,new JObject(),new JObject{["A"]=e},new JObject{["entity"]="A"});
var symbols=new JObject{["text_styles"]=new JObject(),["dimension_styles"]=new JObject{["D"]=style}};dr["symbols_before"]=symbols;dr["symbols_after"]=symbols.DeepClone();
Check(Verify(dimension,dr),"native aligned dimension measurement/style");
corrupt=(JObject)dr.DeepClone();corrupt["after"]["A"]["dimension_line_point"]=Point(7,4);Check(Verify(dimension,corrupt),"native dimension-line representative may shift along its axis");
corrupt=(JObject)dr.DeepClone();corrupt["after"]["A"]["dimension_line_point"]=Point(4,1);Check(!Verify(dimension,corrupt),"dimension line offset change rejected");
corrupt=(JObject)dr.DeepClone();corrupt["after"]["A"]["measurement"]=6;Check(!Verify(dimension,corrupt),"wrong native measurement rejected");
corrupt=(JObject)dr.DeepClone();corrupt["after"]["A"]["type"]="Circle";Check(!Verify(dimension,corrupt),"forged dimension entity type rejected");
corrupt=(JObject)dr.DeepClone();corrupt["symbols_after"]["dimension_styles"]["D"]["text_height"]=2;Check(!Verify(dimension,corrupt),"existing style mutation rejected");
Console.WriteLine($"Foundation extension invariants: {passed} passed");
