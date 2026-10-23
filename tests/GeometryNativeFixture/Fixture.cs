using System;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Bimwright.Dwg.Plugin;
using Newtonsoft.Json.Linq;

public class GeometryNativeFixture
{
    [CommandMethod("CAFGEOMETRYFIXTURE")]
    public void Create()
    {
        string dir=Environment.GetEnvironmentVariable("CAF_GEOMETRY_FIXTURE_DIR");
        if(string.IsNullOrWhiteSpace(dir)||!Directory.Exists(dir))throw new InvalidOperationException("Explicit fixture output directory required");
        string file=Path.Combine(dir,"synthetic.dwg");if(File.Exists(file))throw new InvalidOperationException("Fixture exists; preserve it");
        var report=new JObject{["execution"]="native console fixture preparation/direct reader tests; separate from packaged MCP acceptance"};
        using(var db=new Database(true,true))
        {
            var handles=new JObject();var direct=new JObject();
            using(var tx=db.TransactionManager.StartTransaction())
            {
                var table=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForWrite);
                var model=(BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                void Add(string name,Entity entity){model.AppendEntity(entity);tx.AddNewlyCreatedDBObject(entity,true);handles[name]=entity.Handle.ToString();direct[name]=new FoundationGeometryReader().Read(entity,tx,0);}
                Add("line",new Line(new Point3d(10,20,3),new Point3d(13,24,3)));
                Add("arc_negative_normal",new Arc(new Point3d(100,200,5),-Vector3d.ZAxis,10,.2,2.0));
                Add("circle",new Circle(new Point3d(15,25,4),Vector3d.ZAxis,7));
                Add("ellipse",new Ellipse(new Point3d(30,40,2),Vector3d.ZAxis,new Vector3d(8,0,0),.5,0,2*Math.PI));
                var poly=new Polyline();poly.Normal=new Vector3d(0,1,1).GetNormal();poly.Elevation=7;
                poly.AddVertexAt(0,new Point2d(1,2),.25,0,0);poly.AddVertexAt(1,new Point2d(6,7),0,0,0);poly.AddVertexAt(2,new Point2d(9,2),0,0,0);poly.Closed=true;Add("tilted_polyline",poly);
                Add("polyline3d",new Polyline3d(Poly3dType.SimplePoly,new Point3dCollection(new[]{new Point3d(1,2,3),new Point3d(4,5,6),new Point3d(7,8,9)}),false));
                Add("spline",new Spline(3,true,false,false,new Point3dCollection(new[]{new Point3d(0,0,1),new Point3d(2,4,1),new Point3d(4,4,1),new Point3d(6,0,1)}),new DoubleCollection(new double[]{0,0,0,0,1,1,1,1}),new DoubleCollection(new double[]{1,2,2,1}),1e-9,1e-9));
                var block=new BlockTableRecord{Name="FixtureFrame"};table.Add(block);tx.AddNewlyCreatedDBObject(block,true);
                foreach(double inset in new[]{0d,10d})
                {
                    var boundary=new Polyline();boundary.AddVertexAt(0,new Point2d(inset,inset),0,0,0);boundary.AddVertexAt(1,new Point2d(420-inset,inset),0,0,0);boundary.AddVertexAt(2,new Point2d(420-inset,297-inset),0,0,0);boundary.AddVertexAt(3,new Point2d(inset,297-inset),0,0,0);boundary.Closed=true;
                    block.AppendEntity(boundary);tx.AddNewlyCreatedDBObject(boundary,true);
                }
                Add("transformed_frame",new BlockReference(new Point3d(1000,2000,0),block.ObjectId){Rotation=Math.PI/6,ScaleFactors=new Scale3d(2)});
                var large=new BlockTableRecord{Name="FixtureLarge"};table.Add(large);tx.AddNewlyCreatedDBObject(large,true);
                for(int i=0;i<200;i++){var line=new Line(new Point3d(i,0,0),new Point3d(i,10,0));large.AppendEntity(line);tx.AddNewlyCreatedDBObject(line,true);}
                Add("paged_block",new BlockReference(new Point3d(3000,4000,0),large.ObjectId));
                report["handles"]=handles;report["direct_reader"]=direct;
                tx.Commit();
            }
            db.SaveAs(file,DwgVersion.Current);
        }
        File.WriteAllText(Path.Combine(dir,"fixture.json"),report.ToString());
    }
}
