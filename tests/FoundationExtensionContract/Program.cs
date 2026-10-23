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
bool ReadArgs(string json)=>Bimwright.Dwg.Plugin.Cad.HandleBatchRequest.TryParseCanonicalJson(json,out _,out _);
Check(ReadArgs("{\"handles\":[\"A\"],\"include_geometry\":true}"),"legacy read unchanged");
Check(ReadArgs("{\"handles\":[\"A\"],\"include_geometry\":true,\"geometry_offset\":64}"),"geometry page accepted");
foreach(string offset in new[]{"-1","4097","1.5","true","999999999999999999999999999999"})Check(!ReadArgs("{\"handles\":[\"A\"],\"include_geometry\":true,\"geometry_offset\":"+offset+"}"),"invalid geometry offset rejected "+offset);
Check(!ReadArgs("{\"handles\":[\"A\",\"B\"],\"include_geometry\":true,\"geometry_offset\":0}"),"geometry page requires one handle");
Check(!ReadArgs("{\"handles\":[\"A\"],\"geometry_offset\":0}"),"geometry page requires geometry");
Check(!ReadArgs("{\"handles\":[\"A\"],\"include_geometry\":true,\"unexpected\":0}"),"unknown read option rejected");
JObject Point(double x,double y)=>new(){["x"]=x,["y"]=y,["z"]=0};
JObject Plan(JObject step)=>new(){["schema"]=FoundationDrawingPlan.Schema,["source_role"]="test",["units"]="metres",["steps"]=new JArray(step)};
bool Valid(JObject step){try{FoundationDrawingPlan.Parse(Plan(step).ToString());return true;}catch{return false;}}
JObject Receipt(JObject step,JObject before,JObject after,JObject map,JObject layers=null)=>new(){["schema"]="foundation-plan-receipt-v1",["plan_sha256"]=FoundationDrawingPlan.Hash(Plan(step)),["outcome"]="committed_verified",["effects"]="committed",["commit_invoked"]=true,["commit_returned"]=true,["error"]=JValue.CreateNull(),["objects"]=new JObject{[step.Value<string>("id")]=map},["before"]=before,["after"]=after,["all_layers_before"]=layers??new JObject(),["all_layers_after"]=layers?.DeepClone()??new JObject(),["model_handles_before"]=new JArray(before.Properties().Select(p=>p.Name)),["model_handles_after"]=new JArray(after.Properties().Select(p=>p.Name)),["created_handles"]=new JArray(after.Properties().Select(p=>p.Name).Except(before.Properties().Select(p=>p.Name))),["layers"]=new JObject()};
bool Verify(JObject step,JObject receipt)=>FoundationDrawingPlan.VerifyReceipt(receipt,Plan(step).ToString());

var rawUnitPlan=Plan(new JObject{["id"]="u",["op"]="line",["start"]=new JArray(0,0),["end"]=new JArray(18,18),["layer"]="T",["color"]=7});
var widePolyline=new JObject{["handle"]="P",["type"]="Polyline",["layer"]="T",["color_index"]=7,["constant_width"]=0.08,["vertices"]=new JArray(new JObject{["x"]=0,["y"]=0,["z"]=0,["bulge"]=0,["start_width"]=0.08,["end_width"]=0.08},new JObject{["x"]=10,["y"]=0,["z"]=0,["bulge"]=0,["start_width"]=0.08,["end_width"]=0.08}),["is_closed"]=false};
var wideStep=new JObject{["id"]="wide",["op"]="polyline",["vertices"]=new JArray(new JArray(0,0),new JArray(10,0)),["closed"]=false,["layer"]="T",["color"]=7,["constant_width"]=0.08};
var wideReceipt=Receipt(wideStep,new JObject(),new JObject{["P"]=widePolyline},new JObject{["entity"]="P"});
Check(Verify(wideStep,wideReceipt),"constant-width polyline facts verify");
var wrongWide=(JObject)wideReceipt.DeepClone();wrongWide["after"]["P"]["constant_width"]=0.09;Check(!Verify(wideStep,wrongWide),"wrong constant width rejected");
var widthScale=FoundationDrawingPlan.TransformFacts(widePolyline,new JObject{["op"]="scale",["factor"]=2,["base"]=new JArray(0,0)});
Check(widthScale.Value<double>("constant_width")==0.16&&widthScale["vertices"][0].Value<double>("start_width")==0.16&&widthScale["vertices"][1].Value<double>("end_width")==0.16,"scale includes native polyline widths");
var variableWide=(JObject)widePolyline.DeepClone();variableWide["constant_width"]=JValue.CreateNull();variableWide["vertices"][0]["end_width"]=0.12;
var variableScaled=FoundationDrawingPlan.TransformFacts(variableWide,new JObject{["op"]="scale",["factor"]=2,["base"]=new JArray(0,0)});
Check(variableScaled["constant_width"].Type==JTokenType.Null&&variableScaled["vertices"][0].Value<double>("end_width")==0.24,"variable width remains readable and scales without a fabricated constant");
rawUnitPlan["units"]="raw_drawing_units";rawUnitPlan["expected_insunits"]=4;
Check(FoundationDrawingPlan.Parse(rawUnitPlan.ToString())["steps"][0]["end"][0].Value<int>()==18,"raw coordinates preserved without conversion");
bool InvalidPlan(JObject p){try{FoundationDrawingPlan.Parse(p.ToString());return false;}catch{return true;}}
var unguardedUnits=(JObject)rawUnitPlan.DeepClone();unguardedUnits.Remove("expected_insunits");
Check(!InvalidPlan(unguardedUnits),"raw units accept omitted metadata guard");
Check(FoundationDrawingPlan.UnitMetadataMatches(unguardedUnits,null),"unknown metadata does not block raw operation");
foreach(int unit in new[]{0,4,6,25})Check(FoundationDrawingPlan.UnitMetadataMatches(unguardedUnits,unit),"raw operation ignores auxiliary unit code "+unit);
Check(FoundationDrawingPlan.UnitMetadataMatches(rawUnitPlan,4)&&!FoundationDrawingPlan.UnitMetadataMatches(rawUnitPlan,6)&&!FoundationDrawingPlan.UnitMetadataMatches(rawUnitPlan,null),"opted-in unit check still requires matching metadata");
var unitlessGuard=(JObject)rawUnitPlan.DeepClone();unitlessGuard["expected_insunits"]=0;
Check(FoundationDrawingPlan.UnitMetadataMatches(unitlessGuard,0)&&!FoundationDrawingPlan.UnitMetadataMatches(unitlessGuard,null),"explicit unitless metadata check is distinct from missing metadata");
var legacyUnits=(JObject)unguardedUnits.DeepClone();legacyUnits["units"]="metres";
Check(!InvalidPlan(legacyUnits)&&FoundationDrawingPlan.UnitMetadataMatches(legacyUnits,6)&&FoundationDrawingPlan.UnitMetadataMatches(legacyUnits,0)&&!FoundationDrawingPlan.UnitMetadataMatches(legacyUnits,4),"legacy metre declaration keeps original compatibility");
var badUnits=(JObject)rawUnitPlan.DeepClone();badUnits["expected_insunits"]=JValue.CreateNull();Check(InvalidPlan(badUnits),"supplied null metadata check is invalid");
badUnits=(JObject)rawUnitPlan.DeepClone();badUnits["expected_insunits"]="4";Check(InvalidPlan(badUnits),"supplied string metadata check is invalid");
badUnits=(JObject)rawUnitPlan.DeepClone();badUnits["expected_insunits"]=25;Check(InvalidPlan(badUnits),"invalid unit metadata rejected");
badUnits=(JObject)rawUnitPlan.DeepClone();badUnits["units"]="metres";Check(InvalidPlan(badUnits),"metre mode cannot silently mix raw metadata");
var fontAlias=new JObject{["font_file"]="黑体.ttf",["width_factor"]=0.8,["resolved_font"]=new JObject{["typeface"]="黑体",["bold"]=false,["italic"]=false,["charset"]=134,["pitch_family"]=34}};
var fontAliasDisk=(JObject)fontAlias.DeepClone();fontAliasDisk["font_file"]="simhei.ttf";
Check(FoundationDrawingPlan.TextStyleSaveEquivalent(fontAlias,fontAliasDisk),"save accepts same resolved TTF identity alias");
fontAliasDisk["resolved_font"]["typeface"]="宋体";Check(!FoundationDrawingPlan.TextStyleSaveEquivalent(fontAlias,fontAliasDisk),"actual font change still rejected");
fontAliasDisk=(JObject)fontAlias.DeepClone();fontAliasDisk["width_factor"]=0.7;Check(!FoundationDrawingPlan.TextStyleSaveEquivalent(fontAlias,fontAliasDisk),"font geometry change still rejected");
fontAliasDisk=(JObject)fontAlias.DeepClone();fontAliasDisk["font_file"]="txt.shx";Check(!FoundationDrawingPlan.TextStyleSaveEquivalent(fontAlias,fontAliasDisk),"SHX substitution not treated as TTF alias");

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
var prop=new JObject{["id"]="props",["op"]="set_properties",["targets"]=targets.DeepClone(),["layer"]="Dest",["color"]=4};
var changed=(JObject)circle.DeepClone();changed["layer"]="Dest";changed["color_index"]=4;
var pr=Receipt(prop,new JObject{["A"]=circle.DeepClone()},new JObject{["A"]=changed},new JObject{["A"]="A"});
Check(Valid(prop)&&Verify(prop,pr),"phase2 guarded layer/color without geometry change");
corrupt=(JObject)pr.DeepClone();corrupt["after"]["A"]["radius"]=8;Check(!Verify(prop,corrupt),"property change cannot hide geometry mutation");
invalid=(JObject)prop.DeepClone();invalid["targets"][0]["expected_sha256"]=new string('0',64);Check(!Verify(invalid,pr),"phase2 stale digest rejected");
var arc=new JObject{["id"]="arc",["op"]="arc",["center"]=new JArray(2,3),["radius"]=2,["start_angle"]=300,["sweep_angle"]=120,["layer"]="T",["color"]=2};
Check(Valid(arc),"arc wrapping zero has explicit positive sweep");invalid=(JObject)arc.DeepClone();invalid["sweep_angle"]=360;Check(!Valid(invalid),"full arc must use circle");
var ellipse=new JObject{["id"]="e",["op"]="ellipse",["center"]=new JArray(0,0),["major_axis"]=new JArray(3,4),["radius_ratio"]=.5,["layer"]="T",["color"]=2};Check(Valid(ellipse),"bounded full ellipse");invalid=(JObject)ellipse.DeepClone();invalid["major_axis"]=new JArray(0,0);Check(!Valid(invalid),"zero ellipse axis refused");
var open=new JObject{["id"]="open",["op"]="set_open_polyline",["targets"]=targets.DeepClone(),["vertices"]=new JArray(new JArray(0,0),new JArray(3,4))};Check(Valid(open),"open polyline accepts two vertices");invalid=(JObject)open.DeepClone();invalid["vertices"]=new JArray(new JArray(0,0));Check(!Valid(invalid),"one-vertex continuation refused");
var off=new JObject{["id"]="offset",["op"]="offset",["targets"]=targets.DeepClone(),["distance"]=1.5};
Check(Valid(off)&&Math.Abs(FoundationDrawingPlan.CurveExpected(circle,off).Value<double>("radius")-3.5)<1e-10,"signed circle offset is outward and retains source");
invalid=(JObject)off.DeepClone();invalid["distance"]=0;Check(!Valid(invalid),"zero offset refused");
var mirrored=new JObject{["id"]="mirror",["op"]="mirror_copy",["targets"]=targets.DeepClone(),["axis_start"]=new JArray(0,0),["axis_end"]=new JArray(0,1)};
Check(Math.Abs(FoundationDrawingPlan.CurveExpected(circle,mirrored)["center"].Value<double>("x")+3)<1e-10,"mirror about y axis reflects center x only");
var pdf=new JObject{["id"]="pdf",["op"]="plot_pdf",["layout"]="Model",["expected_sha256"]=new string('A',64),["file_name"]="sheet.pdf"};Check(Valid(pdf),"bounded PDF basename");invalid=(JObject)pdf.DeepClone();invalid["file_name"]="../sheet.pdf";Check(!Valid(invalid),"PDF path escape refused");
var setup=new JObject{["id"]="setup",["op"]="page_setup",["layout"]="Model",["expected_sha256"]=new string('A',64),["device"]="DWG To PDF.pc3",["media"]="A3",["plot_style"]="monochrome.ctb",["rotation"]=0,["plot_area"]="extents",["scale_numerator"]=1,["scale_denominator"]=100,["center"]=true};Check(Valid(setup),"explicit device/style/fixed ratio schema");invalid=(JObject)setup.DeepClone();invalid["plot_area"]="layout";Check(!Valid(invalid),"layout plot cannot silently apply centering");
invalid=(JObject)setup.DeepClone();invalid["scale_denominator"]=0;Check(!Valid(invalid),"zero print denominator refused");
var multi=Plan(pdf);((JArray)multi["steps"]).Add(setup);bool rejected=false;try{FoundationDrawingPlan.Parse(multi.ToString());}catch{rejected=true;}Check(rejected,"file plotting cannot mix with drawing transaction");
var angular=new JObject{["id"]="angle",["op"]="angular_dimension",["center"]=new JArray(0,0),["ray1"]=new JArray(4,0),["ray2"]=new JArray(0,4),["position"]=new JArray(3,3),["dimension_style"]="D",["layer"]="T",["color"]=2};Check(Valid(angular),"explicit CCW angular sector accepted");invalid=(JObject)angular.DeepClone();invalid["position"]=new JArray(-3,-3);Check(!Valid(invalid),"ambiguous outside-wedge angular placement refused");
var tableStep=new JObject{["id"]="table",["op"]="table",["position"]=new JArray(0,0),["cells"]=new JArray(new JArray("a","b"),new JArray("c","d")),["row_height"]=6,["column_width"]=8,["text_height"]=1,["text_style"]="T",["layer"]="T",["color"]=2};Check(Valid(tableStep),"bounded rectangular native table");invalid=(JObject)tableStep.DeepClone();invalid["cells"][1]=new JArray("c");Check(!Valid(invalid),"jagged table cannot reach native writer");
var dxf=new JObject{["id"]="dxf",["op"]="export_dxf",["file_name"]="exchange.dxf",["expected"]=targets.DeepClone()};Check(Valid(dxf),"DXF requires explicit disk-verified source objects");invalid=(JObject)dxf.DeepClone();invalid["expected"]=new JArray();Check(!Valid(invalid),"unverified empty DXF export refused");
invalid=(JObject)pdf.DeepClone();invalid["file_name"]="sheet.pdf\n";Check(!Valid(invalid),"trailing newline cannot escape PDF filename validation");
var block=new JObject{["id"]="block",["op"]="block_define",["name"]="Symbol",["members"]=new JArray(new JObject{["kind"]="line",["start"]=new JArray(0,0),["end"]=new JArray(1,0)},new JObject{["kind"]="attribute",["tag"]="CODE",["text"]="A01",["position"]=new JArray(0,1),["height"]=1})};Check(Valid(block),"bounded local named block contract");invalid=(JObject)block.DeepClone();((JArray)invalid["members"]).Add(invalid["members"][1].DeepClone());Check(!Valid(invalid),"duplicate block attribute tag refused");invalid=(JObject)block.DeepClone();invalid["members"][0]["kind"]="run_code";Check(!Valid(invalid),"block definition cannot embed arbitrary operations");
var layoutFacts=new JObject{["device"]="DWG To PDF.pc3",["media"]="A3",["scale_numerator"]=1};
var saveStep=new JObject{["id"]="save",["op"]="save",["expected"]=targets.DeepClone(),["expected_layouts"]=new JObject{["Model"]=FoundationDrawingPlan.Hash(layoutFacts)}};
var saveReceipt=Receipt(saveStep,new JObject{["A"]=circle.DeepClone()},new JObject{["A"]=circle.DeepClone()},new JObject{["A"]="A"});saveReceipt["saved_file_sha256"]=new string('A',64);saveReceipt["dbmod_after_save"]=0;saveReceipt["layouts_before"]=new JObject{["Model"]=layoutFacts};saveReceipt["layouts_after"]=saveReceipt["layouts_before"].DeepClone();Check(Verify(saveStep,saveReceipt),"whole-document save independently includes page setup");corrupt=(JObject)saveReceipt.DeepClone();corrupt["layouts_after"]["Model"]["scale_numerator"]=2;Check(!Verify(saveStep,corrupt),"saved paper scale mismatch cannot pass entity-only verification");
var many=Plan(open);many["steps"]=new JArray(Enumerable.Range(0,9).Select(i=>new JObject{["id"]="open"+i,["op"]="set_open_polyline",["targets"]=targets.DeepClone(),["vertices"]=new JArray(Enumerable.Range(0,128).Select(n=>new JArray(n,0)))}));rejected=false;try{FoundationDrawingPlan.Parse(many.ToString());}catch{rejected=true;}Check(rejected,"open-polyline updates share the total 1024-vertex budget");
var fontChange=new JObject{["id"]="font",["op"]="set_text_style_font",["name"]="F",["font_file"]="hztxt_e.shx",["big_font_file"]="hztxt.shx",["expected_sha256"]=new string('A',64)};
Check(Valid(fontChange),"explicit SHX style replacement contract");invalid=(JObject)fontChange.DeepClone();invalid["font_file"]="../hztxt_e.shx";Check(!Valid(invalid),"replacement cannot escape font basename");
var fontOld=new JObject{["handle"]="F1",["font_file"]="arial.ttf",["big_font_file"]="",["width_factor"]=.8,["fixed_height"]=0,["is_vertical"]=false};fontChange["expected_sha256"]=FoundationDrawingPlan.Hash(fontOld);
var fontNext=(JObject)fontOld.DeepClone();fontNext["font_file"]="hztxt_e.shx";fontNext["big_font_file"]="hztxt.shx";
var fontReceipt=new JObject{["symbols_before"]=new JObject{["text_styles"]=new JObject{["F"]=fontOld},["dimension_styles"]=new JObject()},["symbols_after"]=new JObject{["text_styles"]=new JObject{["F"]=fontNext},["dimension_styles"]=new JObject()}};
Check(FoundationDrawingPlan.VerifySymbols(fontReceipt,new JArray(fontChange)),"font mutation preserves existing style metrics");corrupt=(JObject)fontReceipt.DeepClone();corrupt["symbols_after"]["text_styles"]["F"]["width_factor"]=1.1;Check(!FoundationDrawingPlan.VerifySymbols(corrupt,new JArray(fontChange)),"font replacement cannot silently change style width");invalid=(JObject)fontChange.DeepClone();invalid["expected_sha256"]=new string('0',64);Check(!FoundationDrawingPlan.VerifySymbols(fontReceipt,new JArray(invalid)),"stale font style digest refused");
var metricOld=new JObject{["handle"]="A",["type"]="DBText",["height"]=5,["width_factor"]=.8,["text"]="标题",["position"]=Point(1,2),["alignment_point"]=Point(10,2),["horizontal_mode"]="TextCenter",["vertical_mode"]="TextBase"};
var metrics=new JObject{["id"]="metrics",["op"]="set_text_metrics",["targets"]=new JArray(new JObject{["handle"]="A",["expected_sha256"]=FoundationDrawingPlan.Hash(metricOld)}),["height_scale"]=1.2,["width_scale"]=1.375};
var metricNext=(JObject)metricOld.DeepClone();metricNext["height"]=6;metricNext["width_factor"]=1.1;metricNext["position"]=Point(-2,2);
var metricReceipt=Receipt(metrics,new JObject{["A"]=metricOld},new JObject{["A"]=metricNext},new JObject{["A"]="A"});Check(Valid(metrics)&&Verify(metrics,metricReceipt),"centered text scales around preserved alignment anchor");corrupt=(JObject)metricReceipt.DeepClone();corrupt["after"]["A"]["alignment_point"]=Point(11,2);Check(!Verify(metrics,corrupt),"moving centered anchor cannot pass metric adjustment");corrupt=(JObject)metricReceipt.DeepClone();corrupt["after"]["A"]["text"]="改字";Check(!Verify(metrics,corrupt),"metrics cannot change text content");
Console.WriteLine($"Foundation extension invariants: {passed} passed");

if(args.Length==1){
 var caseData=JObject.Parse(System.IO.File.ReadAllText(args[0]));var original=JObject.Parse(caseData["job"].Value<string>("result_json"));var canonical=JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(args[0]),"requests/v10-copy-actual-complete-section.json")))["plan"].ToString(Newtonsoft.Json.Formatting.None);
 var fresh=(JObject)original.DeepClone();fresh["outcome"]="committed_verified";var proof=new JObject{["schema"]="foundation-archive-roundoff-recovery-v1",["original_result_sha256"]=FoundationDrawingPlan.Hash(original),["plan_sha256"]=FoundationDrawingPlan.Hash(JObject.Parse(canonical)),["native_readback"]=fresh};
 Check(FoundationDrawingPlan.VerifyReadback(original.ToString(),proof.ToString(),canonical),"archive roundoff complete recovery proof passes");
 var wrong=(JObject)proof.DeepClone();wrong["original_result_sha256"]=new string('0',64);Check(!FoundationDrawingPlan.VerifyReadback(original.ToString(),wrong.ToString(),canonical),"recovery rejects other committed result");
 wrong=(JObject)proof.DeepClone();wrong["native_readback"]["after"]["754"]["vertices"][0]["x"]=270.01;Check(!FoundationDrawingPlan.VerifyReadback(original.ToString(),wrong.ToString(),canonical),"recovery rejects changed geometry");
 wrong=(JObject)proof.DeepClone();wrong["native_readback"]["after"]["2FC"]["color_index"]=1;Check(!FoundationDrawingPlan.VerifyReadback(original.ToString(),wrong.ToString(),canonical),"recovery protects original source properties");
 wrong=(JObject)proof.DeepClone();wrong["native_readback"]["layouts_after"]["Model"]["rotation"]=2;Check(!FoundationDrawingPlan.VerifyReadback(original.ToString(),wrong.ToString(),canonical),"recovery protects layout state");
 Console.WriteLine($"Including recovery: {passed} passed");
}

// Scoped view and raster repair contract checks; no CAD process here.
foreach(string json in new[]{"{\"action\":\"read\"}","{\"action\":\"window\",\"min_x\":100,\"min_y\":200,\"max_x\":140,\"max_y\":240}","{\"action\":\"center\",\"x\":100,\"y\":200,\"height\":40}","{\"action\":\"capture\",\"file_name\":\"view.png\"}"})Check(FoundationViewRequest.Valid(FoundationViewRequest.Canonical(json)),"valid view action");
foreach(string json in new[]{"{\"action\":\"window\",\"min_x\":0,\"min_y\":0,\"max_x\":0,\"max_y\":5}","{\"action\":\"center\",\"x\":1,\"y\":2,\"height\":-1}","{\"action\":\"capture\",\"file_name\":\"../old.png\"}","{\"action\":\"read\",\"command\":\"ZOOM\"}","{\"action\":\"read\",\"pixel_size\":300}"}){bool viewRejected=false;try{FoundationViewRequest.Parse(json);}catch{viewRejected=true;}Check(viewRejected,"invalid view refused");}
var rasterStep=new JObject{["id"]="raster",["op"]="set_raster_path",["targets"]=new JArray(new JObject{["handle"]="A",["expected_sha256"]=new string('A',64)}),["path"]="C:/fixture/image.png"};
Check(Valid(rasterStep),"explicit raster path contract");
rasterStep["path"]="relative.png";Check(!Valid(rasterStep),"relative raster repair refused");
Console.WriteLine($"With view/raster contracts: {passed} passed");
