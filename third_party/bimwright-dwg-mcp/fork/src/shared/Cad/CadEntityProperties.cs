using System.Linq;
// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Bimwright.Dwg.Plugin.Cad
{
    internal static class CadEntityProperties
    {
        internal static object Describe(Entity entity, Transaction tx, bool includeGeometry)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            var result = new Dictionary<string, object>
            {
                ["handle"] = entity.Handle.ToString(),
                ["type"] = GetEntityType(entity),
                ["layer"] = entity.Layer,
                ["color_index"] = (int)entity.ColorIndex
            };

            if (!includeGeometry)
            {
                return result;
            }

            result["linetype"]=entity.Linetype;
            result["lineweight"]=(int)entity.LineWeight;

            AddExtents(result, entity);
            var owner=tx.GetObject(entity.OwnerId,OpenMode.ForRead) as BlockTableRecord;
            result["owner_handle"]=entity.OwnerId.Handle.ToString();
            if(owner!=null&&owner.IsLayout){var layout=(Layout)tx.GetObject(owner.LayoutId,OpenMode.ForRead);result["layout_name"]=layout.LayoutName;}

            switch (entity)
            {
#if ACAD2026
                case Wipeout mask:
                    result["wipeout_boundary"]=mask.GetClipBoundary().Cast<Point2d>().Select(p=>Point(new Point3d(p.X,p.Y,0).TransformBy(mask.PixelToModelTransform))).ToArray();break;
                case RasterImage image when image.GetType()==typeof(RasterImage):
                    var def=(RasterImageDef)tx.GetObject(image.ImageDefId,OpenMode.ForRead);
                    result["source_file"]=def.SourceFileName;result["definition_handle"]=def.Handle.ToString();
                    var axes=image.Orientation;result["origin"]=Point(axes.Origin);result["u_axis"]=new[]{axes.Xaxis.X,axes.Xaxis.Y,axes.Xaxis.Z};result["v_axis"]=new[]{axes.Yaxis.X,axes.Yaxis.Y,axes.Yaxis.Z};
                    result["rotation"]=image.Rotation;result["image_width"]=image.ImageWidth;result["image_height"]=image.ImageHeight;
                    result["is_clipped"]=image.IsClipped;result["brightness"]=image.Brightness;result["contrast"]=image.Contrast;result["fade"]=image.Fade;result["show_image"]=image.ShowImage;
                    var clip=new List<object>();foreach(Point2d pt in image.GetClipBoundary())clip.Add(new[]{pt.X,pt.Y});result["clip_boundary"]=clip;
                    break;
#endif
                case DBPoint point:
                    result["position"]=Point(point.Position);break;
                case Line line:
                    DescribeLine(result, line);
                    break;
                case Circle circle:
                    DescribeCircle(result, circle);
                    break;
                case Arc arc:
                    DescribeArc(result, arc);
                    break;
                case Polyline polyline:
                    DescribePolyline(result, polyline);
                    break;
                case DBText text:
                    DescribeDbText(result, text,tx);
                    break;
                case MText mText:
                    DescribeMText(result, mText,tx);
                    break;
                case MLeader mLeader:
                    DescribeMLeader(result, mLeader);
                    break;
                case Leader leader:
                    DescribeLeader(result, leader, tx);
                    break;
                case Table table:
                    DescribeTable(result, table,tx);
                    break;
                case BlockReference blockReference:
                    DescribeBlockReference(result, blockReference, tx);
                    break;
                case Hatch hatch:
                    DescribeHatch(result, hatch);
                    break;
                case Ellipse ellipse:
                    DescribeEllipse(result, ellipse);
                    break;
#if ACAD2026
                case Dimension dimension:
                    Bimwright.Dwg.Plugin.FoundationPlanWriteHandler.DimensionFacts(result,dimension,tx);
                    break;
#endif
            }

            return result;
        }

        private static void DescribeLine(IDictionary<string, object> result, Line line)
        {
            result["start"] = Point(line.StartPoint);
            result["end"] = Point(line.EndPoint);
            result["length"] = line.StartPoint.DistanceTo(line.EndPoint);
        }

        private static void DescribeCircle(IDictionary<string, object> result, Circle circle)
        {
            result["center"] = Point(circle.Center);
            result["radius"] = circle.Radius;
            result["diameter"] = circle.Radius * 2d;
            result["circumference"] = Math.PI * circle.Radius * 2d;
            result["area"] = Math.PI * circle.Radius * circle.Radius;
        }

        private static void DescribeArc(IDictionary<string, object> result, Arc arc)
        {
            result["center"] = Point(arc.Center);
            result["radius"] = arc.Radius;
            result["start_angle"] = arc.StartAngle;
            result["end_angle"] = arc.EndAngle;
            result["included_angle"] = IncludedAngle(arc.StartAngle, arc.EndAngle);
            if (TryGetCurveLength(arc, out var length))
            {
                result["length"] = length;
            }
        }

        private static void DescribePolyline(IDictionary<string, object> result, Polyline polyline)
        {
            result["vertex_count"] = polyline.NumberOfVertices;
            result["is_closed"] = polyline.Closed;
            // ConstantWidth throws for some variable-width polylines. Keep the
            // entity readable and report each native segment width below.
            try { result["constant_width"] = polyline.ConstantWidth; }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.InvalidInput)
            { result["constant_width"] = null; }

            if (TryGetCurveLength(polyline, out var length))
            {
                result["length"] = length;
            }

            if (TryGetArea(polyline, out var area))
            {
                result["area"] = area;
            }

            var vertices = new List<object>();
            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var point = polyline.GetPoint3dAt(i);
                vertices.Add(new
                {
                    index = i,
                    x = point.X,
                    y = point.Y,
                    z = point.Z,
                      bulge = polyline.GetBulgeAt(i),
                      start_width = polyline.GetStartWidthAt(i),
                      end_width = polyline.GetEndWidthAt(i)
                });
            }

            result["vertices"] = vertices.ToArray();
        }

        private static void DescribeDbText(IDictionary<string, object> result, DBText text,Transaction tx)
        {
            result["text"] = text.TextString;
            result["position"] = Point(text.Position);
            result["height"] = text.Height;
            result["width_factor"] = text.WidthFactor;
            if(text.HorizontalMode!=TextHorizontalMode.TextLeft||text.VerticalMode!=TextVerticalMode.TextBase)result["alignment_point"]=Point(text.AlignmentPoint);
            result["rotation"] = text.Rotation;
            result["text_style"] = ((TextStyleTableRecord)tx.GetObject(text.TextStyleId,OpenMode.ForRead)).Name;
            if(text is AttributeDefinition definition){result["attribute_tag"]=definition.Tag;result["constant"]=definition.Constant;}
            result["horizontal_mode"] = text.HorizontalMode.ToString();
            result["vertical_mode"] = text.VerticalMode.ToString();
        }

        private static void DescribeMText(IDictionary<string, object> result, MText mText,Transaction tx)
        {
            result["text"] = mText.Contents;
            result["location"] = Point(mText.Location);
            result["text_height"] = mText.TextHeight;
            result["width"] = mText.Width;
            result["rotation"] = mText.Rotation;
            result["text_style"] = ((TextStyleTableRecord)tx.GetObject(mText.TextStyleId,OpenMode.ForRead)).Name;
            result["attachment"] = mText.Attachment.ToString();
            result["actual_width"] = mText.ActualWidth;
            result["actual_height"] = mText.ActualHeight;
        }

        private static void DescribeMLeader(IDictionary<string, object> result, MLeader mLeader)
        {
            var mText = mLeader.MText;
            try
            {
                result["text"] = mText != null ? mText.Contents : string.Empty;
            }
            finally
            {
                mText?.Dispose();
            }

            result["text_height"] = mLeader.TextHeight;
            result["leader_count"] = mLeader.LeaderCount;
            result["leader_line_count"] = mLeader.LeaderLineCount;
            try
            {
                result["first_vertex"] = Point(mLeader.GetFirstVertex(0));
                result["last_vertex"] = Point(mLeader.GetLastVertex(0));
            }
            catch
            {
                result["first_vertex"] = null;
                result["last_vertex"] = null;
            }
        }

        private static void DescribeLeader(
            IDictionary<string, object> result,
            Leader leader,
            Transaction tx)
        {
            var vertexCount = leader.NumVertices;
            var vertices = new List<object>();
            var verticesToRead = Math.Min(vertexCount, 64);
            for (var i = 0; i < verticesToRead; i++)
            {
                var point = leader.VertexAt(i);
                vertices.Add(new
                {
                    index = i,
                    x = point.X,
                    y = point.Y,
                    z = point.Z
                });
            }

            result["vertex_count"] = vertexCount;
            result["vertices_wcs"] = vertices.ToArray();
            result["vertices_truncated"] = vertexCount > 64;
            result["is_splined"] = leader.IsSplined;
            result["has_arrow_head"] = leader.HasArrowHead;
            result["has_hook_line"] = leader.HasHookLine;
            result["style_name"] = leader.DimensionStyleName;
            result["text_height"] = leader.Dimtxt;
            result["gap"] = leader.Dimgap;
            result["dimtad"] = leader.Dimtad;
            var offset = leader.AnnotationOffset;
            result["annotation_offset"] = new { x = offset.X, y = offset.Y, z = offset.Z };
            var annotationId = leader.Annotation;
            result["annotation_reactor_present"] = !annotationId.IsNull && tx.GetObject(annotationId, OpenMode.ForRead).GetPersistentReactorIds().Cast<ObjectId>().Contains(leader.ObjectId);
            result["annotation_handle"] = annotationId.IsNull
                ? null
                : tx.GetObject(annotationId, OpenMode.ForRead).Handle.ToString();
        }

        private static void DescribeTable(IDictionary<string, object> result, Table table,Transaction tx)
        {
            result["position"] = Point(table.Position);
            result["rows"] = table.Rows.Count;
            result["columns"] = table.Columns.Count;
            bool complete=table.Rows.Count<=16&&table.Columns.Count<=16&&table.Rows.Count*table.Columns.Count<=128;
            result["cells_complete"]=complete;
            if(complete){var cells=new List<object>();var heights=new List<double>();var widths=new List<double>();var textHeights=new List<object>();for(int row=0;row<table.Rows.Count;row++){var values=new List<string>();var sizes=new List<double?>();for(int col=0;col<table.Columns.Count;col++){values.Add(table.Cells[row,col].TextString);sizes.Add(table.Cells[row,col].TextHeight);}cells.Add(values.ToArray());textHeights.Add(sizes.ToArray());heights.Add(table.Rows[row].Height);}for(int col=0;col<table.Columns.Count;col++)widths.Add(table.Columns[col].Width);result["cells"]=cells.ToArray();result["row_heights"]=heights.ToArray();result["column_widths"]=widths.ToArray();result["cell_text_heights"]=textHeights.ToArray();var styles=new List<object>();for(int row=0;row<table.Rows.Count;row++){var names=new List<string>();for(int col=0;col<table.Columns.Count;col++){ObjectId? styleId=table.Cells[row,col].TextStyleId;names.Add(styleId.HasValue&&!styleId.Value.IsNull?((TextStyleTableRecord)tx.GetObject(styleId.Value,OpenMode.ForRead)).Name:null);}styles.Add(names.ToArray());}result["cell_text_styles"]=styles.ToArray();}
        }

        private static void DescribeBlockReference(
            IDictionary<string, object> result,
            BlockReference blockReference,
            Transaction tx)
        {
            result["position"] = Point(blockReference.Position);
            result["rotation"] = blockReference.Rotation;
            result["scale"] = Scale(blockReference.ScaleFactors);
            result["attribute_count"] = blockReference.AttributeCollection.Count;
            var attributes=new List<object>();bool complete=blockReference.AttributeCollection.Count<=64;
            foreach(ObjectId id in blockReference.AttributeCollection){if(attributes.Count>=64)break;if(tx.GetObject(id,OpenMode.ForRead) is AttributeReference a)attributes.Add(new{handle=a.Handle.ToString(),tag=a.Tag,text=a.TextString,position=Point(a.Position),rotation=a.Rotation,height=a.Height});else complete=false;}
            result["attributes"]=attributes.ToArray();result["attributes_complete"]=complete;result["nested_geometry_coverage"]="not_read";

            var name = TryGetBlockName(blockReference, tx);
            if (!string.IsNullOrEmpty(name))
            {
                result["name"] = name;
            }
        }

        private static void DescribeHatch(IDictionary<string, object> result, Hatch hatch)
        {
            result["pattern_name"] = hatch.PatternName;
            result["pattern_scale"] = hatch.PatternScale;
            result["pattern_angle"] = hatch.PatternAngle;
            result["is_associative"] = hatch.Associative;
            result["loop_count"] = hatch.NumberOfLoops;

            if (TryGetArea(hatch, out var area))
            {
                result["area"] = area;
                result["area_status"] = "available";
            }
            else result["area_status"] = "unavailable";

            // Read stored loops only. Never EvaluateHatch or repair a boundary
            // while serving a query. Explicit bounds apply per hatch entity.
            const int maxLoops = 16, maxVertices = 1024, maxHandles = 64;
            result["loop_geometry_space"] = "wcs";
            result["normal"] = Vector(hatch.Normal);
            result["elevation"] = hatch.Elevation;
            var loops = new List<object>();
            int vertexBudget = maxVertices, handleBudget = maxHandles;
            bool complete = hatch.NumberOfLoops > 0 && hatch.NumberOfLoops <= maxLoops;
            result["loops_truncated"] = hatch.NumberOfLoops > maxLoops;
            for (int index = 0; index < Math.Min(hatch.NumberOfLoops, maxLoops); index++)
            {
                var item = new Dictionary<string, object> { ["index"] = index };
                loops.Add(item);
                try
                {
                    HatchLoop loop = hatch.GetLoopAt(index);
                    item["loop_type"] = loop.LoopType.ToString();
                    item["geometry_kind"] = loop.IsPolyline ? "polyline" : "curve_edges";
                    if (!loop.IsPolyline)
                    {
                        var curves = loop.Curves;
                        int count = Math.Min(curves.Count, vertexBudget);
                        var edges = new List<object>();
                        bool supported = count > 0;
                        for (int edge = 0; edge < count; edge++)
                        {
                            Curve2d curve = curves[edge];
                            var record = new Dictionary<string, object> { ["index"] = edge };
                            edges.Add(record);
                            if (curve is LineSegment2d || curve is CircularArc2d)
                            {
                                record["kind"] = curve is LineSegment2d ? "line" : "circular_arc";
                                record["start"] = HatchPoint(curve.StartPoint, hatch);
                                record["end"] = HatchPoint(curve.EndPoint, hatch);
                                if (curve is CircularArc2d arc)
                                {
                                    record["center"] = HatchPoint(arc.Center, hatch);
                                    record["radius"] = arc.Radius;
                                    record["is_clockwise"] = arc.IsClockWise;
                                }
                            }
                            else
                            {
                                record["kind"] = "unsupported";
                                record["native_type"] = curve.GetType().Name;
                                supported = false;
                            }
                        }
                        vertexBudget -= count;
                        item["edge_count"] = curves.Count;
                        item["edges_wcs"] = edges.ToArray();
                        item["edges_truncated"] = count < curves.Count;
                        item["geometry_status"] = !supported ? "unsupported" : count == curves.Count ? "available" : "incomplete";
                        if (!supported || count != curves.Count) complete = false;
                    }
                    else
                    {
                        var points = loop.Polyline;
                        int count = Math.Min(points.Count, vertexBudget);
                        var vertices = new List<object>();
                        for (int vertex = 0; vertex < count; vertex++)
                        {
                            var point = points[vertex];
                            Point3d world = new Point3d(point.Vertex.X, point.Vertex.Y, hatch.Elevation)
                                .TransformBy(Matrix3d.PlaneToWorld(hatch.Normal));
                            vertices.Add(new { index = vertex, x = world.X, y = world.Y, z = world.Z, bulge = point.Bulge });
                        }
                        vertexBudget -= count;
                        item["vertex_count"] = points.Count;
                        item["vertices_wcs"] = vertices.ToArray();
                        item["vertices_truncated"] = count < points.Count;
                        item["geometry_status"] = count == points.Count && count > 0 ? "available" : "incomplete";
                        if (count != points.Count || count == 0) complete = false;
                    }
                }
                catch
                {
                    item["geometry_status"] = "unavailable";
                    complete = false;
                }
                if (!hatch.Associative)
                {
                    item["association_status"] = "not_associative";
                    item["associated_handles"] = Array.Empty<string>();
                    continue;
                }
                try
                {
                    ObjectIdCollection ids = hatch.GetAssociatedObjectIdsAt(index);
                    int count = Math.Min(ids.Count, handleBudget);
                    var handles = new List<string>();
                    for (int handle = 0; handle < count; handle++)
                    {
                        if (ids[handle].IsNull || ids[handle].IsErased)
                            throw new InvalidOperationException("Associated boundary identity unavailable.");
                        handles.Add(ids[handle].Handle.ToString());
                    }
                    handleBudget -= count;
                    item["associated_handles"] = handles.ToArray();
                    item["associated_handle_count"] = ids.Count;
                    item["associated_handles_truncated"] = count < ids.Count;
                    item["association_status"] = count == ids.Count && count > 0 ? "available" : "incomplete";
                    if (count != ids.Count || count == 0) complete = false;
                }
                catch
                {
                    item["association_status"] = "unavailable";
                    item["associated_handles"] = null;
                    complete = false;
                }
            }
            result["loops"] = loops.ToArray();
            result["loop_read_status"] = complete ? "complete" : "incomplete";
        }

        private static object HatchPoint(Point2d point, Hatch hatch) => Point(
            new Point3d(point.X, point.Y, hatch.Elevation).TransformBy(Matrix3d.PlaneToWorld(hatch.Normal)));

        private static void DescribeEllipse(IDictionary<string, object> result, Ellipse ellipse)
        {
            result["center"] = Point(ellipse.Center);
            result["major_axis"] = Vector(ellipse.MajorAxis);
            result["radius_ratio"] = ellipse.RadiusRatio;
            result["start_angle"] = ellipse.StartAngle;
            result["end_angle"] = ellipse.EndAngle;

            if (TryGetCurveLength(ellipse, out var length))
            {
                result["length"] = length;
            }
        }

        private static void AddExtents(IDictionary<string, object> result, Entity entity)
        {
            try
            {
                var extents = entity.GeometricExtents;
                result["extents"] = new
                {
                    min = Point(extents.MinPoint),
                    max = Point(extents.MaxPoint)
                };
            }
            catch
            {
                result["extents"] = null;
            }
        }

        private static string GetEntityType(Entity entity)
        {
            if (entity is Line) return "Line";
            if (entity is Circle) return "Circle";
            if (entity is Arc) return "Arc";
            if (entity is Polyline) return "Polyline";
            if (entity is DBText) return entity.GetType().Name;
            if (entity is MText) return "MText";
            if (entity is MLeader) return "MLeader";
            if (entity is Table) return "Table";
            if (entity is BlockReference) return "BlockReference";
            if (entity is Hatch) return "Hatch";
            if (entity is Ellipse) return "Ellipse";
            return entity.GetType().Name;
        }

        private static string TryGetBlockName(BlockReference blockReference, Transaction tx)
        {
            if (tx == null || blockReference.BlockTableRecord.IsNull)
            {
                return null;
            }

            try
            {
                var blockTableRecord = (BlockTableRecord)tx.GetObject(blockReference.BlockTableRecord, OpenMode.ForRead);
                return blockTableRecord.Name;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetCurveLength(Curve curve, out double length)
        {
            length = 0d;
            try
            {
                length = curve.GetDistanceAtParameter(curve.EndParam) -
                    curve.GetDistanceAtParameter(curve.StartParam);
                return true;
            }
            catch
            {
                length = 0d;
                return false;
            }
        }

        private static bool TryGetArea(Entity entity, out double area)
        {
            area = 0d;
            try
            {
                switch (entity)
                {
                    case Polyline polyline:
                        area = polyline.Area;
                        return true;
                    case Hatch hatch:
                        area = hatch.Area;
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                area = 0d;
                return false;
            }
        }

        private static double IncludedAngle(double startAngle, double endAngle)
        {
            var angle = endAngle - startAngle;
            while (angle < 0d)
            {
                angle += Math.PI * 2d;
            }

            return angle;
        }

        private static object Point(Point3d point)
        {
            return new
            {
                x = point.X,
                y = point.Y,
                z = point.Z
            };
        }

        private static object Vector(Vector3d vector)
        {
            return new
            {
                x = vector.X,
                y = vector.Y,
                z = vector.Z,
                length = vector.Length
            };
        }

        private static object Scale(Scale3d scale)
        {
            return new
            {
                x = scale.X,
                y = scale.Y,
                z = scale.Z
            };
        }
    }
}
