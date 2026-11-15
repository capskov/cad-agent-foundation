using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json.Linq;
public class ViewRasterFixture
{
 [CommandMethod("CAFVIEWRASTERFIXTURE")]
 public void Create()
 {
  string dir=Environment.GetEnvironmentVariable("CAF_VIEW_FIXTURE_DIR");
  if(string.IsNullOrEmpty(dir)||!Directory.Exists(dir))throw new InvalidOperationException("explicit fresh fixture directory required");
  string dwg=Path.Combine(dir,"source.dwg"),oldImage=Path.Combine(dir,"fixture-old.png"),newImage=Path.Combine(dir,"fixture-new.png");
  if(File.Exists(dwg)||File.Exists(oldImage)||File.Exists(newImage))throw new InvalidOperationException("fixture exists");
  using(var bitmap=new Bitmap(320,200)){using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Color.LightBlue);graphics.FillRectangle(Brushes.DarkGreen,50,70,220,60);bitmap.Save(oldImage,ImageFormat.Png);}
  var handles=new JObject();
  using(var db=new Database(true,true))
  {
   db.Insunits=UnitsValue.Meters;
   using(var tx=db.TransactionManager.StartTransaction())
   {
    var model=(BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),OpenMode.ForWrite);
    void Add(string name,Entity e){model.AppendEntity(e);tx.AddNewlyCreatedDBObject(e,true);handles[name]=e.Handle.ToString();}
    Add("line",new Line(new Point3d(100,200,0),new Point3d(140,200,0)));
    var blocks=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForWrite);var block=new BlockTableRecord{Name="VisibleTextFixture"};blocks.Add(block);tx.AddNewlyCreatedDBObject(block,true);
    var text=new MText{Contents="Fixture visible text",Location=new Point3d(0,0,0),TextHeight=2,Width=50};block.AppendEntity(text);tx.AddNewlyCreatedDBObject(text,true);
    Add("text_block",new BlockReference(new Point3d(100,230,0),block.ObjectId));
    if(RasterImageDef.GetImageDictionary(db).IsNull)RasterImageDef.CreateImageDictionary(db);
    var dictionary=(DBDictionary)tx.GetObject(RasterImageDef.GetImageDictionary(db),OpenMode.ForWrite);
    var definition=new RasterImageDef{SourceFileName=oldImage};definition.Load();dictionary.SetAt("fixture",definition);tx.AddNewlyCreatedDBObject(definition,true);
    var image=new RasterImage{ImageDefId=definition.ObjectId,Orientation=new CoordinateSystem3d(new Point3d(90,190,0),new Vector3d(64,0,0),new Vector3d(0,40,0)),ShowImage=true};Add("raster",image);
    RasterImage.EnableReactors(true);image.AssociateRasterDef(definition);
    tx.Commit();
   }
   db.SaveAs(dwg,DwgVersion.Current);
  }
  File.Move(oldImage,newImage);
  File.WriteAllText(Path.Combine(dir,"fixture.json"),new JObject{["handles"]=handles,["replacement_image"]=newImage,["missing_original"]=oldImage,["preparation"]="native console synthetic preparation, not MCP acceptance"}.ToString());
 }
}
