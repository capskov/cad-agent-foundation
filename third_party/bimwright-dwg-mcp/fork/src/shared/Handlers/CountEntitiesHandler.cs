using System;
using Autodesk.AutoCAD.ApplicationServices;
using Bimwright.Dwg.Plugin.Cad;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin.Handlers
{
    public class CountEntitiesHandler : IAcadCommand
    {
        public string Name => "count_entities";
        public string Description => "Count top-level entities in model space or an explicit layout by optional type, layer, and color filters.";
        public CommandSchema Schema => CommandSchemas.CountEntities;

        public CommandResult Execute(Document doc, JToken parameters)
        {
            if (!QueryHandlerInput.TryCreateOptions(
                parameters,
                includeGeometry: false,
                readLimit: false,
                out var options,
                out var error))
            {
                return CommandResult.Fail(error);
            }

            try
            {
                var count = CadQueryService.CountEntities(doc.Database, options);
                return CommandResult.Success(new { count, scope=options.Layout==null?"model_space":"explicit_layout_top_level", layout=options.Layout??"Model" });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("failed to count entities: " + ex.Message);
            }
        }
    }
}
