using System;
using Autodesk.AutoCAD.ApplicationServices;
using Bimwright.Dwg.Plugin.Cad;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin.Handlers
{
    public class SelectByLayerHandler : IAcadCommand
    {
        public string Name => "select_by_layer";
        public string Description => "Return handles for top-level entities in model space or an explicit layout on a layer without changing pickfirst selection.";
        public CommandSchema Schema => CommandSchemas.SelectByLayer;

        public CommandResult Execute(Document doc, JToken parameters)
        {
            var layer = QueryHandlerInput.ReadOptionalString(parameters, "layer");
            if (string.IsNullOrWhiteSpace(layer))
            {
                return CommandResult.Fail("layer must be a non-empty string");
            }

            if (!QueryHandlerInput.TryReadOptionalColorIndex(parameters, out var colorIndex, out var colorError))
            {
                return CommandResult.Fail(colorError);
            }

            if (!QueryHandlerInput.TryReadOptionalLimit(parameters, readLimit: true, out var limit, out var limitError))
            {
                return CommandResult.Fail(limitError);
            }

            if(!QueryHandlerInput.TryReadOptionalLayout(parameters,out string layout,out string layoutError))return CommandResult.Fail(layoutError);
            var options = new CadEntityQueryOptions(
                QueryHandlerInput.ReadOptionalString(parameters, "entity_type"),
                layer,
                colorIndex,
                limit,
                includeGeometry: false,
                layout: layout);

            try
            {
                var handles = CadQueryService.QueryHandles(doc.Database, options);
                return CommandResult.Success(new { count = handles.Count, handles, scope=options.Layout==null?"model_space":"explicit_layout_top_level", layout=options.Layout??"Model" });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("failed to select by layer: " + ex.Message);
            }
        }
    }
}
