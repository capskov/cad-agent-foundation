// Bounded Foundation operations, capskov 2026. Apache-2.0.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin
{
 internal static partial class FoundationPlanWriteHandler
 {
  static JObject SymbolFacts(Database db,Transaction tx)
  {
   var text=new JObject();var dims=new JObject();
   foreach(ObjectId id in (TextStyleTable)tx.GetObject(db.TextStyleTableId,OpenMode.ForRead))
   {
    if(text.Count>=512)throw new InvalidOperationException("text_style_catalog_limit_512");
    var r=(TextStyleTableRecord)tx.GetObject(id,OpenMode.ForRead);
    var font=r.Font;
    text[r.Name]=new JObject{["handle"]=id.Handle.ToString(),["font_file"]=Path.GetFileName(r.FileName),["big_font_file"]=Path.GetFileName(r.BigFontFileName),["width_factor"]=r.XScale,["fixed_height"]=r.TextSize,["is_vertical"]=r.IsVertical,["resolved_font"]=new JObject{["typeface"]=font.TypeFace,["bold"]=font.Bold,["italic"]=font.Italic,["charset"]=font.CharacterSet,["pitch_family"]=font.PitchAndFamily}};
   }
   foreach(ObjectId id in (DimStyleTable)tx.GetObject(db.DimStyleTableId,OpenMode.ForRead))
   {
    if(dims.Count>=512)throw new InvalidOperationException("dimension_style_catalog_limit_512");
    var r=(DimStyleTableRecord)tx.GetObject(id,OpenMode.ForRead);var ts=(TextStyleTableRecord)tx.GetObject(r.Dimtxsty,OpenMode.ForRead);
    dims[r.Name]=new JObject{["handle"]=id.Handle.ToString(),["text_style"]=ts.Name,["text_height"]=r.Dimtxt,["arrow_size"]=r.Dimasz,["overall_scale"]=r.Dimscale,["gap"]=r.Dimgap,["decimal_places"]=r.Dimdec,["extension_origin_offset"]=r.Dimexo,["extension_beyond"]=r.Dimexe,["decimal_separator"]=((char)r.Dimdsep).ToString(),["dimension_color"]=(int)r.Dimclrd.ColorIndex,["extension_color"]=(int)r.Dimclre.ColorIndex,["text_color"]=(int)r.Dimclrt.ColorIndex};
   }
   return new JObject{["text_styles"]=text,["dimension_styles"]=dims,["block_definitions"]=BlockDefinitions(db,tx),["xrefs"]=XrefPathFacts(db,tx),["paper_viewports"]=PaperViewportFacts(db,tx),["paper_texts"]=PaperTextFacts(db,tx),["paper_blocks"]=PaperBlockFacts(db,tx)};
  }
  internal static JObject ReadCatalog(Document doc)
  {
   using var tx=doc.Database.TransactionManager.StartTransaction();var layers=LayerFacts(doc.Database,tx);var symbols=SymbolFacts(doc.Database,tx);
   if(layers.Count>512)throw new InvalidOperationException("layer_catalog_limit_512");
   var resources=ResourceCatalog(doc.Database,tx);var layoutSettings=LayoutFacts(doc.Database,tx);var layoutDigests=new JObject();foreach(var row in layoutSettings.Properties())layoutDigests[row.Name]=FoundationDrawingPlan.Hash(row.Value);
   var blockHashes=new JObject();foreach(var row in ((JObject)symbols["block_definitions"]).Properties())blockHashes[row.Name]=FoundationDrawingPlan.Hash(row.Value);
   var hashes=new JObject();foreach(var r in layers.Properties())hashes[r.Name]=FoundationDrawingPlan.Hash(r.Value);
   var paperEntities=PaperEntityCatalog(doc.Database,tx);tx.Abort();return new JObject{["api_revision"]="phase2-local-1",["operation_constraints"]=new JObject{["offset"]="standalone_2D_Line_or_Circle;positive_left_or_outward;source_retained",["mirror_copy"]="standalone_2D_Line_or_Circle;source_retained",["set_text_format"]="standalone_baseline_left_DBText_or_MText;MText_requires_width",["set_text_style_font"]="existing_local_non_shape_style;fresh_style_sha256;installed_SHX_pair;all_references;no_inline_font_override_rewrite",["set_text_metrics"]="standalone_planar_model_DBText;preserved_baseline_or_alignment_anchor;no_fit_align_attributes_or_reactors;1_to_32_targets",["set_dimension_text"]="four_original_dimension_kinds;manual_text_movement_2",["angular_dimension"]="explicit_CCW_sector_less_than_180_degrees;no_live_source_association",["block_definitions"]="256_named;32_members_each;128_detailed_members_shared_budget;unsupported_or_truncated_is_incomplete",["table"]="16_rows_or_columns;128_cells;unmerged;native_minimum_row_height",["plot_pdf"]="current_layout_only;PDF_PC3_only;outputs_basename;no_overwrite;header_and_hash_verified",["export_dxf"]="outputs_basename;no_overwrite;1_to_64_selected_entities_independent_DxfIn_readback"},["print_feature_status"]=new JObject{["M1"]="partial_PC3_media_and_device_style_discovery",["M2"]="implemented_bounded_layout_settings_read",["M3"]="bounded_layout_or_extents_fixed_scale",["M4"]="native_current_layout_device_media_preflight;preview_is_external_PDF_render",["M5"]="single_PDF_only_no_image_export",["M6"]="deferred",["M7"]="deferred"},["runtime_framework"]=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,["process_architecture"]=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),["managed_cad_api_version"]=typeof(Database).Assembly.GetName().Version.ToString(),["unit_metadata_role"]="auxiliary_Agent_interpretation_precision_and_plot_guidance;no_conversion;raw_mode_guard_opt_in",["write_units"]="raw_drawing_units_recommended_or_legacy_metres",["deferred_operations"]=new JArray("M6_batch_plot","M7_physical_printer_job_monitor"),["resources"]=resources,["plan_limits"]=new JObject{["steps"]=16,["entity_outcomes"]=64,["semantic_bytes"]=131072,["units"]="raw_drawing_units_optional_expected_insunits_or_legacy_metres"},["layout_settings"]=layoutSettings,["layout_digests"]=layoutDigests,["printing"]=PrintCatalog(doc),["observation"]="bind_time",["complete"]=true,["plan_operations"]=new JArray(FoundationDrawingPlan.Operations),["writes_enabled"]=App.FoundationWriteEnabled,["layers"]=layers,["layer_digests"]=hashes,["block_definition_digests"]=blockHashes,["block_definitions"]=symbols["block_definitions"],["text_styles"]=symbols["text_styles"],["dimension_styles"]=symbols["dimension_styles"],["xref_definitions"]=symbols["xrefs"],["paper_entity_catalog"]=paperEntities,["paper_viewports"]=symbols["paper_viewports"],["paper_texts"]=symbols["paper_texts"],["paper_blocks"]=symbols["paper_blocks"]};
  }
  static ObjectId TextStyle(Database db,Transaction tx,string name)
  {var t=(TextStyleTable)tx.GetObject(db.TextStyleTableId,OpenMode.ForRead);if(!t.Has(name))throw new InvalidOperationException("text_style_missing:"+name);return t[name];}
  static ObjectId DimStyle(Database db,Transaction tx,string name)
  {var t=(DimStyleTable)tx.GetObject(db.DimStyleTableId,OpenMode.ForRead);if(!t.Has(name))throw new InvalidOperationException("dimension_style_missing:"+name);return t[name];}
  static void ApplyTextStyle(Database db,Transaction tx,Entity e,JObject step)
  {
   if(step["text_style"]==null)return;var id=TextStyle(db,tx,step.Value<string>("text_style"));
   if(e is DBText t)t.TextStyleId=id;else if(e is MText m)m.TextStyleId=id;
  }
  internal static void DimensionFacts(IDictionary<string,object> facts,Dimension d,Transaction tx)
  {
   var style=(DimStyleTableRecord)tx.GetObject(d.DimensionStyle,OpenMode.ForRead);
   facts["dimension_style"]=style.Name;facts["measurement"]=d.Measurement;facts["dimension_text"]=d.DimensionText;facts["text_position"]=new{x=d.TextPosition.X,y=d.TextPosition.Y,z=d.TextPosition.Z};facts["using_default_text_position"]=d.UsingDefaultTextPosition;facts["text_movement"]=d.Dimtmove;
   facts["text_height"]=d.Dimtxt;facts["arrow_size"]=d.Dimasz;facts["overall_scale"]=d.Dimscale;facts["gap"]=d.Dimgap;facts["decimal_places"]=d.Dimdec;facts["dimension_color"]=(int)d.Dimclrd.ColorIndex;facts["extension_color"]=(int)d.Dimclre.ColorIndex;facts["text_color"]=(int)d.Dimclrt.ColorIndex;
   object P(Point3d p)=>new{x=p.X,y=p.Y,z=p.Z};
   switch(d)
   {
    case RotatedDimension r:facts["dimension_kind"]="linear_dimension";facts["start"]=P(r.XLine1Point);facts["end"]=P(r.XLine2Point);facts["dimension_line_point"]=P(r.DimLinePoint);facts["rotation"]=r.Rotation;break;
    case LineAngularDimension2 a:facts["dimension_kind"]="angular_dimension";facts["line1_start"]=P(a.XLine1Start);facts["line1_end"]=P(a.XLine1End);facts["line2_start"]=P(a.XLine2Start);facts["line2_end"]=P(a.XLine2End);facts["arc_point"]=P(a.ArcPoint);break;
    case AlignedDimension a:facts["dimension_kind"]="aligned_dimension";facts["start"]=P(a.XLine1Point);facts["end"]=P(a.XLine2Point);facts["dimension_line_point"]=P(a.DimLinePoint);break;
    case RadialDimension r:facts["dimension_kind"]="radial_dimension";facts["center"]=P(r.Center);facts["chord"]=P(r.ChordPoint);facts["leader_length"]=r.LeaderLength;break;
    case DiametricDimension d2:facts["dimension_kind"]="diameter_dimension";facts["center"]=P(new Point3d((d2.ChordPoint.X+d2.FarChordPoint.X)/2,(d2.ChordPoint.Y+d2.FarChordPoint.Y)/2,0));facts["chord"]=P(d2.ChordPoint);facts["leader_length"]=d2.LeaderLength;break;
   }
  }
  static void RefreshChangedFontText(Database db,Transaction tx,JObject plan)
  {
   var names=((JArray)plan["steps"]).Cast<JObject>().Where(s=>s.Value<string>("op")=="set_text_style_font").Select(s=>s.Value<string>("name")).ToHashSet();if(names.Count==0)return;
   var styles=(TextStyleTable)tx.GetObject(db.TextStyleTableId,OpenMode.ForRead);var ids=names.Select(n=>styles[n]).ToHashSet();int scanned=0;
   foreach(ObjectId bid in (BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead))
   {
    var block=(BlockTableRecord)tx.GetObject(bid,OpenMode.ForRead);if(block.IsFromExternalReference||block.IsFromOverlayReference)continue;
    foreach(ObjectId oid in block)
    {
     if(++scanned>100000)throw new InvalidOperationException("font_refresh_entity_limit_100000");
     if(tx.GetObject(oid,OpenMode.ForRead) is not MText mt||!ids.Contains(mt.TextStyleId))continue;
     // Native MTEXT retains old font layout metrics until reflow. Reparse the
     // unchanged contents in the same transaction so save/readback agree.
     string contents=mt.Contents;var location=mt.Location;mt.UpgradeOpen();mt.DisableUndoRecording(false);mt.Contents=contents;mt.Location=location;mt.RecordGraphicsModified(true);
     if(mt.Contents!=contents)throw new InvalidOperationException("font_refresh_must_preserve_mtext_contents");
    }
   }
  }
  static bool ExecuteExtendedStep(Database db,Transaction tx,JObject step,JObject map,Dictionary<string,ObjectId> refs,List<string> created,JObject before)
  {
   var op=step.Value<string>("op");string id=step.Value<string>("id");
   ObjectId Add(Entity e)
   {var oid=Append(db,tx,e,step.Value<string>("layer"),step.Value<int>("color"));map["entity"]=oid.Handle.ToString();created.Add(oid.Handle.ToString());refs[id]=oid;return oid;}
   Color C(string key)=>Color.FromColorIndex(ColorMethod.ByAci,(short)step.Value<int>(key));
   switch(op)
   {
    case "set_text_style_font":
     var fontTable=(TextStyleTable)tx.GetObject(db.TextStyleTableId,OpenMode.ForRead);string fontName=step.Value<string>("name");
     if(!fontTable.Has(fontName))throw new InvalidOperationException("existing_text_style_required");
     var fontRecord=(TextStyleTableRecord)tx.GetObject(fontTable[fontName],OpenMode.ForRead);
     if(fontRecord.IsDependent||fontRecord.IsShapeFile)throw new InvalidOperationException("local_text_style_required_not_shape_or_xref");
     if(FoundationDrawingPlan.Hash(SymbolFacts(db,tx)["text_styles"][fontName])!=step.Value<string>("expected_sha256"))throw new InvalidOperationException("text_style_changed:"+fontName);
     foreach(string key in new[]{"font_file","big_font_file"})
     {string f=step.Value<string>(key);if(f.Length==0)continue;string resolved=HostApplicationServices.Current.FindFile(f,db,FindFileHint.Default);if(string.IsNullOrEmpty(resolved)||!File.Exists(resolved))throw new InvalidOperationException("installed_font_unavailable:"+f);}
     fontRecord.UpgradeOpen();fontRecord.DisableUndoRecording(false);fontRecord.FileName=step.Value<string>("font_file");fontRecord.BigFontFileName=step.Value<string>("big_font_file");map["name"]=fontName;break;
    case "text_style":
     var tt=(TextStyleTable)tx.GetObject(db.TextStyleTableId,OpenMode.ForRead);string name=step.Value<string>("name");
     if(tt.Has(name)){map["name"]=name;break;}
     string Find(string font)
     {if(string.IsNullOrEmpty(font))return "";string found=HostApplicationServices.Current.FindFile(font,db,FindFileHint.Default);if(string.IsNullOrEmpty(found)||!File.Exists(found))throw new InvalidOperationException("installed_font_unavailable:"+font);return font;}
     string font=Find(step.Value<string>("font_file")),big=Find(step.Value<string>("big_font_file"));
     tt.UpgradeOpen();tt.DisableUndoRecording(false);var text=new TextStyleTableRecord{Name=name,FileName=font,BigFontFileName=big,XScale=step.Value<double>("width_factor"),TextSize=0,IsVertical=false};tt.Add(text);tx.AddNewlyCreatedDBObject(text,true);text.DisableUndoRecording(false);map["name"]=name;break;
    case "dimension_style":
     var dt=(DimStyleTable)tx.GetObject(db.DimStyleTableId,OpenMode.ForRead);string dn=step.Value<string>("name");
     if(dt.Has(dn)){map["name"]=dn;break;}
     var dim=new DimStyleTableRecord();dim.CopyFrom((DimStyleTableRecord)tx.GetObject(db.Dimstyle,OpenMode.ForRead));dim.Name=dn;dim.Dimtxsty=TextStyle(db,tx,step.Value<string>("text_style"));dim.Dimtxt=step.Value<double>("text_height");dim.Dimasz=step.Value<double>("arrow_size");dim.Dimscale=step.Value<double>("overall_scale");dim.Dimgap=step.Value<double>("gap");dim.Dimdec=step.Value<int>("decimal_places");if(step["extension_origin_offset"]!=null)dim.Dimexo=step.Value<double>("extension_origin_offset");if(step["extension_beyond"]!=null)dim.Dimexe=step.Value<double>("extension_beyond");if(step["decimal_separator"]!=null)dim.Dimdsep=step.Value<string>("decimal_separator")[0];dim.Dimclrd=C("dimension_color");dim.Dimclre=C("extension_color");dim.Dimclrt=C("text_color");
     dt.UpgradeOpen();dt.DisableUndoRecording(false);dt.Add(dim);tx.AddNewlyCreatedDBObject(dim,true);dim.DisableUndoRecording(false);map["name"]=dn;break;
    case "set_layer":
     var lt=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForRead);string ln=step.Value<string>("name");
     if(!lt.Has(ln))throw new InvalidOperationException("existing_layer_required");var layer=(LayerTableRecord)tx.GetObject(lt[ln],OpenMode.ForRead);
     if(layer.IsDependent)throw new InvalidOperationException("xref_layer_state_unsupported");
     if(ln=="0"&&(step.Value<bool>("is_off")||step.Value<bool>("is_frozen")))throw new InvalidOperationException("shared_zero_layer_cannot_be_hidden");
     if(db.Clayer==layer.ObjectId&&(step.Value<bool>("is_off")||step.Value<bool>("is_frozen")))throw new InvalidOperationException("current_layer_cannot_be_hidden");
     var current=LayerFacts(db,tx)[ln];if(FoundationDrawingPlan.Hash(current)!=step.Value<string>("expected_sha256"))throw new InvalidOperationException("layer_changed");
     layer.UpgradeOpen();layer.DisableUndoRecording(false);layer.Color=C("color");layer.IsOff=step.Value<bool>("is_off");layer.IsFrozen=step.Value<bool>("is_frozen");layer.IsLocked=step.Value<bool>("is_locked");layer.IsPlottable=step.Value<bool>("is_plottable");map["layer"]=ln;break;
    case "set_callout_format":
     var leaderFormat=tx.GetObject(Resolve(db,step.Value<string>("handle")),OpenMode.ForWrite) as Leader;var labelFormat=tx.GetObject(Resolve(db,step.Value<string>("text_handle")),OpenMode.ForWrite) as MText;
     if(leaderFormat==null||labelFormat==null||leaderFormat.Annotation!=labelFormat.ObjectId)throw new InvalidOperationException("linked_LEADER_MTEXT_required");
     var location=labelFormat.Location;leaderFormat.DisableUndoRecording(false);labelFormat.DisableUndoRecording(false);leaderFormat.DimensionStyle=DimStyle(db,tx,step.Value<string>("dimension_style"));leaderFormat.Dimtxt=step.Value<double>("height");leaderFormat.Dimgap=step.Value<double>("gap");leaderFormat.Color=C("leader_color");
     labelFormat.TextStyleId=TextStyle(db,tx,step.Value<string>("text_style"));labelFormat.TextHeight=step.Value<double>("height");labelFormat.Width=step.Value<double>("width");labelFormat.Color=C("text_color");if(step["attachment"]!=null)labelFormat.Attachment=Enum.Parse<AttachmentPoint>(step.Value<string>("attachment"));labelFormat.Location=location;map["leader"]=leaderFormat.Handle.ToString();map["text"]=labelFormat.Handle.ToString();break;
    case "angular_dimension":
     var angular=new LineAngularDimension2(Point(step["center"]),Point(step["ray1"]),Point(step["center"]),Point(step["ray2"]),Point(step["position"]),"",DimStyle(db,tx,step.Value<string>("dimension_style")));Add(angular);angular.RecomputeDimensionBlock(true);break;
    case "linear_dimension":case "aligned_dimension":case "radial_dimension":case "diameter_dimension":
     var style=DimStyle(db,tx,step.Value<string>("dimension_style"));Dimension entity;
     if(op=="linear_dimension")entity=new RotatedDimension((step.Value<double?>("rotation")??0)*Math.PI/180,Point(step["start"]),Point(step["end"]),Point(step["position"]),"",style);
     else if(op=="aligned_dimension")entity=new AlignedDimension(Point(step["start"]),Point(step["end"]),Point(step["position"]),"",style);
     else if(op=="radial_dimension")entity=new RadialDimension(Point(step["center"]),Point(step["chord"]),0,"",style);
     else{var a=Point(step["chord"]);var c=Point(step["center"]);entity=new DiametricDimension(a,new Point3d(2*c.X-a.X,2*c.Y-a.Y,0),0,"",style);}
     Add(entity);entity.RecomputeDimensionBlock(true);
     if(op is "radial_dimension" or "diameter_dimension")
     {var c=Point(step["center"]);var chord=Point(step["chord"]);entity.UsingDefaultTextPosition=false;entity.TextPosition=chord+(chord-c).GetNormal()*step.Value<double>("text_offset");entity.RecomputeDimensionBlock(true);}break;
    case "rotate":case "scale":case "copy":
     var targets=(JArray)step["targets"];var work=new Dictionary<string,ObjectId>();
     if(op=="copy")
     {
      var ids=new ObjectIdCollection(targets.Cast<JObject>().Select(t=>Resolve(db,t.Value<string>("handle"))).ToArray());var mapping=new IdMapping();db.DeepCloneObjects(ids,SymbolUtilityServices.GetBlockModelSpaceId(db),mapping,false);
      foreach(ObjectId oid in ids){var pair=mapping[oid];if(!pair.IsCloned)throw new InvalidOperationException("copy_not_cloned");work[oid.Handle.ToString()]=pair.Value;map[oid.Handle.ToString()]=pair.Value.Handle.ToString();created.Add(pair.Value.Handle.ToString());}
     }
     else foreach(JObject t in targets){string h=t.Value<string>("handle");work[h]=Resolve(db,h);map[h]=h;}
     foreach(var row in work)
     {
      var e=(Entity)tx.GetObject(row.Value,OpenMode.ForWrite);e.DisableUndoRecording(false);var expected=FoundationDrawingPlan.TransformFacts((JObject)before[row.Key],step);
      Point3d P(string f)=>new Point3d(expected[f].Value<double>("x"),expected[f].Value<double>("y"),expected[f].Value<double>("z"));
      switch(e)
      {
       case Line l:l.StartPoint=P("start");l.EndPoint=P("end");break;
       case Circle c:c.Center=P("center");c.Radius=expected.Value<double>("radius");break;
       case DBText t:t.Position=P("position");t.Height=expected.Value<double>("height");t.Rotation=expected.Value<double>("rotation");break;
       case MText t:t.Location=P("location");t.TextHeight=expected.Value<double>("text_height");t.Width=expected.Value<double>("width");t.Rotation=expected.Value<double>("rotation");break;
       case Polyline line:
        if(line.Normal!=Vector3d.ZAxis||line.Elevation!=0)throw new InvalidOperationException("2d_wcs_polyline_required");
        var v=(JArray)expected["vertices"];for(int i=0;i<line.NumberOfVertices;i++)line.SetPointAt(i,new Point2d(v[i].Value<double>("x"),v[i].Value<double>("y")));break;
       case Leader leader:
        var vs=(JArray)expected["vertices_wcs"];for(int i=0;i<leader.NumVertices;i++)leader.SetVertexAt(i,new Point3d(vs[i].Value<double>("x"),vs[i].Value<double>("y"),vs[i].Value<double>("z")));leader.Dimtxt=expected.Value<double>("text_height");leader.Dimgap=expected.Value<double>("gap");break;
       case Hatch:break;
       default:throw new InvalidOperationException("unsupported_transform_entity");
      }
     }
     // Leader updates can move their annotation; restore its exact planned pose last.
     foreach(var row in work)if(tx.GetObject(row.Value,OpenMode.ForWrite) is MText m)
     {var e=FoundationDrawingPlan.TransformFacts((JObject)before[row.Key],step);m.Location=new Point3d(e["location"].Value<double>("x"),e["location"].Value<double>("y"),e["location"].Value<double>("z"));m.Rotation=e.Value<double>("rotation");m.TextHeight=e.Value<double>("text_height");m.Width=e.Value<double>("width");}
     foreach(var oid in work.Values)if(tx.GetObject(oid,OpenMode.ForWrite) is Hatch h)
     {var ids=h.GetAssociatedObjectIdsAt(0);h.RemoveLoopAt(0);h.Associative=true;h.AppendLoop(HatchLoopTypes.External,ids);h.EvaluateHatch(true);h.RecordGraphicsModified(true);}
     break;
    default:return ExecutePhase2(db,tx,step,map,refs,created)||ExecuteCurve(db,tx,step,map,refs,created,before)||ExecuteBlock(db,tx,step,map,refs,created);
   }
   return true;
  }
 }
}
