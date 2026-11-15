// Reuses the governed transaction, fresh digests and independent verifier.
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin
{
 internal static partial class FoundationPlanWriteHandler
 {
  static bool ExecutePhase2(Database db,Transaction tx,JObject s,JObject map,Dictionary<string,ObjectId> refs,List<string> created)
  {
   string op=s.Value<string>("op");
   void Add(Entity e)
   {e.SetDatabaseDefaults(db);var id=Append(db,tx,e,s.Value<string>("layer"),s.Value<int>("color"));map["entity"]=id.Handle.ToString();refs[s.Value<string>("id")]=id;created.Add(id.Handle.ToString());}
   switch(op)
   {
    case "page_setup":SetPageSetup(db,tx,s,map);break;
    case "point":Add(new DBPoint(Point(s["position"])));break;
    case "table":
     var cells=(JArray)s["cells"];var newTable=new Table();newTable.SetDatabaseDefaults(db);newTable.TableStyle=db.Tablestyle;newTable.Position=Point(s["position"]);newTable.SetSize(cells.Count,((JArray)cells[0]).Count);newTable.SetRowHeight(s.Value<double>("row_height"));newTable.SetColumnWidth(s.Value<double>("column_width"));
     newTable.UnmergeCells(CellRange.Create(newTable,0,0,newTable.Rows.Count-1,newTable.Columns.Count-1));
     for(int row=0;row<newTable.Rows.Count;row++)for(int col=0;col<newTable.Columns.Count;col++){newTable.Cells[row,col].TextString=cells[row][col].Value<string>();newTable.Cells[row,col].TextHeight=s.Value<double>("text_height");newTable.Cells[row,col].TextStyleId=TextStyle(db,tx,s.Value<string>("text_style"));}
     Add(newTable);newTable.GenerateLayout();break;
    case "arc":
     double start=FoundationDrawingPlan.NormalizeAngle(s.Value<double>("start_angle")*Math.PI/180);Add(new Arc(Point(s["center"]),Vector3d.ZAxis,s.Value<double>("radius"),start,start+s.Value<double>("sweep_angle")*Math.PI/180));break;
    case "ellipse":Add(new Ellipse(Point(s["center"]),Vector3d.ZAxis,Point(s["major_axis"])-Point3d.Origin,s.Value<double>("radius_ratio"),0,2*Math.PI));break;
    case "rectangle":
     var min=Point(s["min"]);var max=Point(s["max"]);var poly=new Polyline();int i=0;foreach(var p in new[]{new Point2d(min.X,min.Y),new Point2d(max.X,min.Y),new Point2d(max.X,max.Y),new Point2d(min.X,max.Y)})poly.AddVertexAt(i++,p,0,0,0);poly.Closed=true;Add(poly);break;
    case "set_hatch_pattern":
     string hh=s.Value<string>("hatch_handle");var hatch=tx.GetObject(Resolve(db,hh),OpenMode.ForWrite) as Hatch;if(hatch==null)throw new InvalidOperationException("associated_hatch_required");
     hatch.DisableUndoRecording(false);hatch.SetHatchPattern(HatchPatternType.PreDefined,s.Value<string>("pattern"));hatch.PatternScale=s.Value<double>("scale");hatch.PatternAngle=FoundationDrawingPlan.NormalizeAngle(s.Value<double>("angle"));hatch.EvaluateHatch(true);hatch.RecordGraphicsModified(true);foreach(JObject t in (JArray)s["targets"])map[t.Value<string>("handle")]=t["handle"];break;
    case "set_raster_path":
     var rasterTarget=(JObject)s["targets"][0];string rh=rasterTarget.Value<string>("handle");
     if(tx.GetObject(Resolve(db,rh),OpenMode.ForRead) is not RasterImage ri||ri.GetType()!=typeof(RasterImage)||ri.ImageDefId.IsNull)throw new InvalidOperationException("native_raster_reference_required_not_proxy_or_wipeout");
     string path=s.Value<string>("path");if(!System.IO.File.Exists(path))throw new InvalidOperationException("replacement_image_missing");
     int references=0,scanned=0;foreach(ObjectId bid in (BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead)){
      var b=(BlockTableRecord)tx.GetObject(bid,OpenMode.ForRead);foreach(ObjectId eid in b){if(++scanned>100000)throw new InvalidOperationException("raster_reference_scan_limit");if(tx.GetObject(eid,OpenMode.ForRead) is RasterImage other&&other.ImageDefId==ri.ImageDefId)references++;}
     }
     if(references!=1)throw new InvalidOperationException("shared_raster_definition_refused_path_change_affects_multiple_references");
     var definition=(RasterImageDef)tx.GetObject(ri.ImageDefId,OpenMode.ForWrite);definition.DisableUndoRecording(false);definition.SourceFileName=path;definition.Load();
     if(!definition.IsLoaded)throw new InvalidOperationException("native_image_load_failed");
     map[rh]=rh;break;
    case "set_properties":case "set_open_polyline":case "set_text_format":case "set_text_metrics":case "set_dimension_text":case "set_table_cell":case "set_line":case "set_circle":
     foreach(JObject target in (JArray)s["targets"])
     {
      string handle=target.Value<string>("handle");var e=(Entity)tx.GetObject(Resolve(db,handle),OpenMode.ForWrite);e.DisableUndoRecording(false);
      if(op=="set_line")
      {if(e is not Line l||l.StartPoint.Z!=0||l.EndPoint.Z!=0||l.Thickness!=0||l.GetPersistentReactorIds().Count!=0)throw new InvalidOperationException("standalone_zero_thickness_WCS_Line_required");l.StartPoint=Point(s["start"]);l.EndPoint=Point(s["end"]);}
      else if(op=="set_circle")
      {if(e is not Circle c||c.Center.Z!=0||c.Normal!=Vector3d.ZAxis||c.Thickness!=0||c.GetPersistentReactorIds().Count!=0)throw new InvalidOperationException("standalone_zero_thickness_WCS_Circle_required");c.Center=Point(s["center"]);c.Radius=s.Value<double>("radius");}
      else if(op=="set_table_cell")
      {
       if(e is not Table t||t.Rows.Count>16||t.Columns.Count>16||t.Rows.Count*t.Columns.Count>128||s.Value<int>("row")>=t.Rows.Count||s.Value<int>("column")>=t.Columns.Count)throw new InvalidOperationException("bounded_table_and_existing_cell_required");
       t.Cells[s.Value<int>("row"),s.Value<int>("column")].TextString=s.Value<string>("text");t.GenerateLayout();
      }
      else if(op=="set_properties")
      {
       var table=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForRead);string name=s.Value<string>("layer");
       if(!table.Has(name))throw new InvalidOperationException("destination_layer_must_exist");
       var layer=(LayerTableRecord)tx.GetObject(table[name],OpenMode.ForRead);if(layer.IsDependent||layer.IsLocked||layer.IsOff||layer.IsFrozen)throw new InvalidOperationException("destination_layer_must_be_local_visible_unlocked");
       e.LayerId=layer.ObjectId;e.Color=Color.FromColorIndex(ColorMethod.ByAci,(short)s.Value<int>("color"));
       if(s["linetype"]!=null){var types=(LinetypeTable)tx.GetObject(db.LinetypeTableId,OpenMode.ForRead);if(!types.Has(s.Value<string>("linetype")))throw new InvalidOperationException("installed_drawing_linetype_required");e.LinetypeId=types[s.Value<string>("linetype")];}
       if(s["lineweight"]!=null)e.LineWeight=(LineWeight)s.Value<int>("lineweight");
      }
      else if(op=="set_open_polyline")
      {
       if(e is not Polyline p||p.Closed||p.Normal!=Vector3d.ZAxis||p.Elevation!=0||p.ConstantWidth!=0||Enumerable.Range(0,p.NumberOfVertices).Any(n=>p.GetStartWidthAt(n)!=0||p.GetEndWidthAt(n)!=0))throw new InvalidOperationException("open_zero_width_2d_wcs_polyline_required");
       var vs=(JArray)s["vertices"];while(p.NumberOfVertices>vs.Count)p.RemoveVertexAt(p.NumberOfVertices-1);
       for(int n=0;n<vs.Count;n++){var v=(JArray)vs[n];var xy=new Point2d(v[0].Value<double>(),v[1].Value<double>());double b=v.Count==3?v[2].Value<double>():0;if(n<p.NumberOfVertices){p.SetPointAt(n,xy);p.SetBulgeAt(n,b);}else p.AddVertexAt(n,xy,b,0,0);}
      }
      else if(op=="set_text_metrics")
      {
       if(e is not DBText mt||e is AttributeDefinition||e is AttributeReference||mt.Normal!=Vector3d.ZAxis||mt.Position.Z!=0||mt.Thickness!=0||mt.HorizontalMode is TextHorizontalMode.TextAlign or TextHorizontalMode.TextFit||mt.GetPersistentReactorIds().Count!=0)throw new InvalidOperationException("standalone_planar_DBText_required_without_fit_or_align");
       bool left=mt.HorizontalMode==TextHorizontalMode.TextLeft&&mt.VerticalMode==TextVerticalMode.TextBase;
       var anchor=left?mt.Position:mt.AlignmentPoint;if(anchor.Z!=0)throw new InvalidOperationException("planar_text_anchor_required");
       double height=mt.Height*s.Value<double>("height_scale"),width=mt.WidthFactor*s.Value<double>("width_scale");
       if(height<=0||height>1e9||width<0.01||width>100)throw new InvalidOperationException("resulting_text_metrics_out_of_range");
       mt.Height=height;mt.WidthFactor=width;if(left)mt.Position=anchor;else{mt.AlignmentPoint=anchor;mt.AdjustAlignment(db);}mt.RecordGraphicsModified(true);
      }
      else if(op=="set_text_format")
      {
       if(e is DBText t)
       {
        if(s["width"]!=null||t.Normal!=Vector3d.ZAxis||t.Position.Z!=0||t.Thickness!=0||t.HorizontalMode!=TextHorizontalMode.TextLeft||t.VerticalMode!=TextVerticalMode.TextBase)throw new InvalidOperationException("standalone_baseline_left_2d_DBText_required_without_width");
        t.TextStyleId=TextStyle(db,tx,s.Value<string>("text_style"));t.Position=Point(s["position"]);t.Height=s.Value<double>("height");t.Rotation=FoundationDrawingPlan.NormalizeAngle(s.Value<double>("rotation")*Math.PI/180);
       }
       else if(e is MText m)
       {
        if(s["width"]==null||m.Normal!=Vector3d.ZAxis||m.Location.Z!=0)throw new InvalidOperationException("standalone_2d_MText_requires_width");
        m.TextStyleId=TextStyle(db,tx,s.Value<string>("text_style"));m.Location=Point(s["position"]);m.TextHeight=s.Value<double>("height");m.Width=s.Value<double>("width");m.Rotation=FoundationDrawingPlan.NormalizeAngle(s.Value<double>("rotation")*Math.PI/180);
       }
       else throw new InvalidOperationException("standalone_text_required");
      }
      else
      {
       if(e is not Dimension d||d is not (RotatedDimension or AlignedDimension or RadialDimension or DiametricDimension))throw new InvalidOperationException("supported_native_dimension_required");
       d.Dimtmove=2;d.DimensionText=s.Value<string>("text");d.Dimtxt=s.Value<double>("height");d.UsingDefaultTextPosition=false;d.TextPosition=Point(s["position"]);d.RecomputeDimensionBlock(true);
      }
      map[handle]=handle;
     }
     break;
    default:return false;
   }
   return true;
  }
 }
}
