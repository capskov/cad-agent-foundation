// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Bimwright.Dwg.Plugin.Cad;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin.Handlers
{
    public class GetEntityPropertiesHandler : IAcadCommand
    {
        public string Name => "get_entity_properties";
        public string Description => "Return properties for entities identified by handle.";
        public CommandSchema Schema => CommandSchemas.GetEntityProperties;

        public CommandResult Execute(Document doc, JToken parameters)
        {
            if (!HandleBatchRequest.TryCreate(parameters, out var request, out var requestError))
            {
                return CommandResult.Fail(requestError);
            }

            var results = new List<object>();
            var db = doc.Database;
#if ACAD2026
            var geometryReader = new FoundationGeometryReader();
#endif

            using (var tx = db.TransactionManager.StartTransaction())
            {
                foreach (var handle in request.Handles)
                {
                    if (!CadHandleResolver.TryResolve(db, handle, out var objectId, out var error))
                    {
                        results.Add(new { handle, ok = false, entity = (object)null, error });
                        continue;
                    }

                    try
                    {
                        var obj = tx.GetObject(objectId, OpenMode.ForRead);
                        var entity = obj as Entity;
                        if (entity == null)
                        {
                            results.Add(new
                            {
                                handle,
                                ok = false,
                                entity = (object)null,
                                error = $"object is not an entity: {obj.GetType().Name}"
                            });
                            continue;
                        }

                        object facts=CadEntityProperties.Describe(entity,tx,request.IncludeGeometry);
#if ACAD2026
                        JObject geometry=null;
                        if(request.IncludeGeometry){
                            var revision=Bimwright.Dwg.Plugin.View.DrawingRevision.For(doc);
                            long beforeRevision=revision.Revision;
                            geometry=geometryReader.Read(entity,tx,request.GeometryOffset);
                            geometry["document_session_id"]=revision.SessionId;
                            geometry["drawing_revision"]=beforeRevision;
                            if(revision.Revision!=beforeRevision)throw new InvalidOperationException("drawing_changed_during_geometry_read");
                        }
#endif
                        // Native decomposition may refresh derived block text extents. Capture facts after it.
                        facts=CadEntityProperties.Describe(entity,tx,request.IncludeGeometry);
                        string digest=null,digestError=null;
                        try{digest=Bimwright.Dwg.Shared.Execution.FoundationDrawingPlan.Hash(JToken.FromObject(request.IncludeGeometry?facts:CadEntityProperties.Describe(entity,tx,true)));}catch(Exception ex){digestError=ex.Message;}
                        results.Add(new
                        {
                            handle,
                            ok = true,
                            entity_digest = digest,
                            digest_error = digestError,
                            entity = facts,
#if ACAD2026
                            geometry_read = geometry,
                            reference_read = entity is RasterImage raster && raster.GetType()==typeof(RasterImage) ? FoundationPlanWriteHandler.RasterReferenceStatus(doc,raster,tx) : null,
#endif
                            error = (string)null
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { handle, ok = false, entity = (object)null, error = ex.Message });
                    }
                }

                tx.Commit();
            }

            return CommandResult.Success(new { entities = results });
        }
    }
}
