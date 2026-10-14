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
    text[r.Name]=new JObject{["handle"]=id.Handle.ToString(),["font_file"]=Path.GetFileName(r.FileName),["big_font_file"]=Path.GetFileName(r.BigFontFileName),["width_factor"]=r.XScale,["fixed_height"]=r.TextSize,["is_vertical"]=r.IsVertical};
   }
   foreach(ObjectId id in (DimStyleTable)tx.GetObject(db.DimStyleTableId,OpenMode.ForRead))
   {
    if(dims.Count>=512)throw new InvalidOperationException("dimension_style_catalog_limit_512");
    var r=(DimStyleTableRecord)tx.GetObject(id,OpenMode.ForRead);var ts=(TextStyleTableRecord)tx.GetObject(r.Dimtxsty,OpenMode.ForRead);
    dims[r.Name]=new JObject{["handle"]=id.Handle.ToString(),["text_style"]=ts.Name,["text_height"]=r.Dimtxt,["arrow_size"]=r.Dimasz,["overall_scale"]=r.Dimscale,["gap"]=r.Dimgap,["decimal_places"]=r.Dimdec,["dimension_color"]=(int)r.Dimclrd.ColorIndex,["extension_color"]=(int)r.Dimclre.ColorIndex,["text_color"]=(int)r.Dimclrt.ColorIndex};
   }
   return new JObject{["text_styles"]=text,["dimension_styles"]=dims};
  }
  internal static JObject ReadCatalog(Document doc)
  {
   using var tx=doc.Database.TransactionManager.StartTransaction();var layers=LayerFacts(doc.Database,tx);var symbols=SymbolFacts(doc.Database,tx);
   if(layers.Count>512)throw new InvalidOperationException("layer_catalog_limit_512");
   var hashes=new JObject();foreach(var r in layers.Properties())hashes[r.Name]=FoundationDrawingPlan.Hash(r.Value);
   tx.Abort();return new JObject{["observation"]="bind_time",["complete"]=true,["plan_operations"]=new JArray(FoundationDrawingPlan.Operations),["writes_enabled"]=App.FoundationWriteEnabled,["layers"]=layers,["layer_digests"]=hashes,["text_styles"]=symbols["text_styles"],["dimension_styles"]=symbols["dimension_styles"]};
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
   facts["dimension_style"]=style.Name;facts["measurement"]=d.Measurement;facts["dimension_text"]=d.DimensionText;facts["text_position"]=new{x=d.TextPosition.X,y=d.TextPosition.Y,z=d.TextPosition.Z};facts["using_default_text_position"]=d.UsingDefaultTextPosition;
   facts["text_height"]=d.Dimtxt;facts["arrow_size"]=d.Dimasz;facts["overall_scale"]=d.Dimscale;facts["gap"]=d.Dimgap;facts["decimal_places"]=d.Dimdec;facts["dimension_color"]=(int)d.Dimclrd.ColorIndex;facts["extension_color"]=(int)d.Dimclre.ColorIndex;facts["text_color"]=(int)d.Dimclrt.ColorIndex;
   object P(Point3d p)=>new{x=p.X,y=p.Y,z=p.Z};
   switch(d)
   {
    case RotatedDimension r:facts["dimension_kind"]="linear_dimension";facts["start"]=P(r.XLine1Point);facts["end"]=P(r.XLine2Point);facts["dimension_line_point"]=P(r.DimLinePoint);facts["rotation"]=r.Rotation;break;
    case AlignedDimension a:facts["dimension_kind"]="aligned_dimension";facts["start"]=P(a.XLine1Point);facts["end"]=P(a.XLine2Point);facts["dimension_line_point"]=P(a.DimLinePoint);break;
    case RadialDimension r:facts["dimension_kind"]="radial_dimension";facts["center"]=P(r.Center);facts["chord"]=P(r.ChordPoint);facts["leader_length"]=r.LeaderLength;break;
    case DiametricDimension d2:facts["dimension_kind"]="diameter_dimension";facts["center"]=P(new Point3d((d2.ChordPoint.X+d2.FarChordPoint.X)/2,(d2.ChordPoint.Y+d2.FarChordPoint.Y)/2,0));facts["chord"]=P(d2.ChordPoint);facts["leader_length"]=d2.LeaderLength;break;
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
     var dim=new DimStyleTableRecord();dim.CopyFrom((DimStyleTableRecord)tx.GetObject(db.Dimstyle,OpenMode.ForRead));dim.Name=dn;dim.Dimtxsty=TextStyle(db,tx,step.Value<string>("text_style"));dim.Dimtxt=step.Value<double>("text_height");dim.Dimasz=step.Value<double>("arrow_size");dim.Dimscale=step.Value<double>("overall_scale");dim.Dimgap=step.Value<double>("gap");dim.Dimdec=step.Value<int>("decimal_places");dim.Dimclrd=C("dimension_color");dim.Dimclre=C("extension_color");dim.Dimclrt=C("text_color");
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
    default:return false;
   }
   return true;
  }
 }
}
