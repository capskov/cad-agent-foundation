using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin
{
    // Transient native decomposition only. Never append, erase, transform or open for write.
    internal sealed class FoundationGeometryReader
    {
        private int nodesLeft = 4096, valuesLeft = 60000;
        internal JObject Read(Entity source, Transaction tx, int offset)
        {
            var leaves = new JArray(); var issues = new JArray();
            Visit(source, tx, "0", 0, leaves, issues);
            var page=new JArray();int bytes=0;int next=offset;
            for(;next<leaves.Count && page.Count<64;next++){
                var leaf=leaves[next];int size=System.Text.Encoding.UTF8.GetByteCount(leaf.ToString(Newtonsoft.Json.Formatting.None));
                if(bytes+size>48000){if(page.Count==0){issues.Add(new JObject{["path"]=leaf["path"],["error"]="single_primitive_exceeds_page_limit"});next++;}break;}
                page.Add(leaf.DeepClone());bytes+=size;
            }
            return new JObject {
                ["schema"]="foundation-geometry-read-v1", ["coordinate_system"]="WCS",
                ["units"]="raw_drawing_units", ["source_handle"]=source.Handle.ToString(),
                ["status"]=issues.Count==0 ? (leaves.Count==0?"empty":"complete") : (leaves.Count==0?"unavailable":"partial"),
                ["order_semantics"]="native collection order and polyline vertex order; no inferred connectivity or design order",
                ["representation"]=source is ProxyEntity ? "saved_proxy_graphics; may differ from original design geometry" : "native_geometry",
                ["primitives"]=page, ["issues"]=new JArray(System.Linq.Enumerable.Take(issues,128)),
                ["issue_count"]=issues.Count,["issues_truncated"]=issues.Count>128,
                ["extracted_primitive_count"]=leaves.Count,["offset"]=offset,["has_more"]=next<leaves.Count,
                ["next_offset"]=next<leaves.Count ? new JValue(next) : JValue.CreateNull(),
                ["original_class_name"]=source is ProxyEntity proxy?proxy.OriginalClassName:null,
                ["application_description"]=source is ProxyEntity proxy2?proxy2.ApplicationDescription:null,
                ["limits"]=new JObject{["batch_nodes"]=4096,["batch_scalar_values"]=60000,["depth"]=12}
            };
        }
        private static JArray P(Point3d p)=>new JArray(p.X,p.Y,p.Z);
        private static JArray V(Vector3d v)=>new JArray(v.X,v.Y,v.Z);
        private void Reserve(int count) { if(count>valuesLeft)throw new InvalidOperationException("batch_scalar_limit"); valuesLeft-=count; }
        private void Visit(Entity e, Transaction tx, string path, int depth, JArray leaves, JArray issues)
        {
            if(nodesLeft--<=0){Issue("batch_node_limit");return;}
            if(depth>12){Issue("depth_limit");return;}
            var row=new JObject{["path"]=path,["type"]=e.GetType().Name};
            void Issue(string message)=>issues.Add(new JObject{["path"]=path,["type"]=e.GetType().Name,["error"]=message});
            try
            {
                Reserve(24);
                if(e is ProxyEntity proxy){row["original_class_name"]=proxy.OriginalClassName;row["application_description"]=proxy.ApplicationDescription;}
                switch(e)
                {
                    case Line line: row["start"]=P(line.StartPoint);row["end"]=P(line.EndPoint); break;
                    case Arc arc:
                        row["center"]=P(arc.Center);row["normal"]=V(arc.Normal);row["radius"]=arc.Radius;
                        row["start_angle"]=arc.StartAngle;row["end_angle"]=arc.EndAngle;
                        row["start"]=P(arc.StartPoint);row["end"]=P(arc.EndPoint);row["total_angle"]=arc.TotalAngle;break;
                    case Circle circle: row["center"]=P(circle.Center);row["normal"]=V(circle.Normal);row["radius"]=circle.Radius;row["closed"]=true;break;
                    case Ellipse ellipse:
                        row["center"]=P(ellipse.Center);row["normal"]=V(ellipse.Normal);row["major_axis"]=V(ellipse.MajorAxis);
                        row["minor_axis"]=V(ellipse.MinorAxis);row["start_parameter"]=ellipse.StartParam;row["end_parameter"]=ellipse.EndParam;row["closed"]=ellipse.Closed;break;
                    case Polyline pl:
                        Reserve(pl.NumberOfVertices*6);var vertices=new JArray();
                        for(int i=0;i<pl.NumberOfVertices;i++) vertices.Add(new JObject{["point"]=P(pl.GetPoint3dAt(i)),["bulge"]=pl.GetBulgeAt(i),["start_width"]=pl.GetStartWidthAt(i),["end_width"]=pl.GetEndWidthAt(i)});
                        row["vertices"]=vertices;row["normal"]=V(pl.Normal);row["closed"]=pl.Closed;break;
                    case Polyline3d pl3:
                        var points=new JArray();foreach(ObjectId id in pl3){Reserve(3);points.Add(P(((PolylineVertex3d)tx.GetObject(id,OpenMode.ForRead)).Position));}
                        // Simple 3D polylines have linear segments; fitted forms must not be presented as linear.
                        row["vertices"]=points;row["polyline_type"]=pl3.PolyType.ToString();row["closed"]=pl3.Closed;break;
                    case Spline spline:
                        var nurbs=spline.NurbsData;var nativeKnots=nurbs.GetKnots();var nativeWeights=nurbs.GetWeights();
                        Reserve(spline.NumControlPoints*4+nativeKnots.Count);var controls=new JArray();var weights=new JArray();var knots=new JArray();
                        for(int i=0;i<spline.NumControlPoints;i++){controls.Add(P(spline.GetControlPointAt(i)));weights.Add(spline.IsRational?nativeWeights[i]:1d);}
                        foreach(double knot in nativeKnots)knots.Add(knot);
                        row["degree"]=spline.Degree;row["rational"]=spline.IsRational;row["periodic"]=spline.IsPeriodic;row["closed"]=spline.Closed;
                        row["control_points"]=controls;row["weights"]=weights;row["knots"]=knots;row["start_parameter"]=spline.StartParam;row["end_parameter"]=spline.EndParam;break;
                    case DBText text when text is not AttributeDefinition:
                        if(text.TextString.Length>8192){Issue("text_length_limit_8192");return;}
                        row["text"]=text.TextString;row["position"]=P(text.Position);row["rotation"]=text.Rotation;row["height"]=text.Height;row["width_factor"]=text.WidthFactor;row["text_style"]=text.TextStyleName;break;
                    case MText text:
                        if(text.Contents.Length>8192){Issue("text_length_limit_8192");return;}
                        row["text"]=text.Text;row["contents"]=text.Contents;row["position"]=P(text.Location);row["rotation"]=text.Rotation;row["height"]=text.TextHeight;row["width"]=text.Width;row["text_style"]=text.TextStyleName;break;
                    case DBPoint point:row["position"]=P(point.Position);break;
                    default:
                        if(e is DBText || e is MText || e is AttributeDefinition){Issue("non_geometric_text_not_expanded");return;}
                        var children=new DBObjectCollection();
                        try
                        {
                            e.Explode(children);
                            if(children.Count==0)Issue("explode_returned_no_entities");
                            for(int i=0;i<children.Count;i++)
                            {
                                if(nodesLeft<=0){Issue("batch_node_limit; remaining children omitted");break;}
                                if(children[i] is Entity child)Visit(child,tx,path+"/"+i,depth+1,leaves,issues);
                                else Issue("non_entity_explode_child");
                            }
                        }
                        finally {foreach(DBObject child in children)child.Dispose();children.Dispose();}
                        return;
                }
                if(e is Curve curve){row["start"]=P(curve.StartPoint);row["end"]=P(curve.EndPoint);row["closed"]=curve.Closed;try{row["length"]=curve.GetDistanceAtParameter(curve.EndParam)-curve.GetDistanceAtParameter(curve.StartParam);}catch(Exception ex){row["length_error"]=ex.Message;}}
                leaves.Add(row);
            }
            catch(Exception ex){Issue(ex.GetType().Name+": "+ex.Message);}
        }
    }
}
