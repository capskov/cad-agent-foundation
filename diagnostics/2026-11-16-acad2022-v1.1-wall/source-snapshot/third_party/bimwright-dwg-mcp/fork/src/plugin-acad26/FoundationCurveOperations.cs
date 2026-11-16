using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Bimwright.Dwg.Shared.Execution;
using Newtonsoft.Json.Linq;
namespace Bimwright.Dwg.Plugin
{
 internal static partial class FoundationPlanWriteHandler
 {
  static bool ExecuteCurve(Database db,Transaction tx,JObject step,JObject map,Dictionary<string,ObjectId> refs,List<string> created,JObject before)
  {
   string op=step.Value<string>("op");if(op!="offset"&&op!="mirror_copy")return false;
   string h=step["targets"][0].Value<string>("handle");var source=(Entity)tx.GetObject(Resolve(db,h),OpenMode.ForRead);
   if(source.GetPersistentReactorIds().Count!=0)throw new InvalidOperationException("curve_edit_requires_standalone_entity");
   bool planar=source switch{Line l=>l.StartPoint.Z==0&&l.EndPoint.Z==0&&l.Thickness==0,Circle c=>c.Normal==Vector3d.ZAxis&&c.Center.Z==0&&c.Thickness==0,_=>false};if(!planar)throw new InvalidOperationException("curve_edit_requires_zero_thickness_WCS_Line_or_Circle");
   // Shared independent geometry contract; construct native entities and retain all source geometry.
   var expected=FoundationDrawingPlan.CurveExpected((JObject)before[h],step);
   Point3d P(string key)=>new Point3d(expected[key].Value<double>("x"),expected[key].Value<double>("y"),0);
   Entity e=source is Line?new Line(P("start"),P("end")):new Circle(P("center"),Vector3d.ZAxis,expected.Value<double>("radius"));
   e.SetDatabaseDefaults(db);e.SetPropertiesFrom(source);var id=Append(db,tx,e,source.Layer,source.ColorIndex);map["entity"]=id.Handle.ToString();map["source"]=h;refs[step.Value<string>("id")]=id;created.Add(id.Handle.ToString());return true;
  }
 }
}
