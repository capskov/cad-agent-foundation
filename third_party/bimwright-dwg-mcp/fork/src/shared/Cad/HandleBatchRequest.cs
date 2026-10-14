using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin.Cad
{
    internal sealed class HandleBatchRequest
    {
        internal const int MaxHandles = 64;

        private HandleBatchRequest(IReadOnlyList<string> handles, bool includeGeometry)
        {
            Handles = handles;
            IncludeGeometry = includeGeometry;
        }

        internal IReadOnlyList<string> Handles { get; }
        internal bool IncludeGeometry { get; }

        internal static bool TryCreate(JToken parameters, out HandleBatchRequest request, out string error)
        {
            request = null;
            error = null;

            var obj = parameters as JObject;
            if (obj == null)
            {
                error = "params must be an object";
                return false;
            }

            var handlesToken = obj["handles"];
            var handleArray = handlesToken as JArray;
            if (handleArray == null)
            {
                error = "handles must be an array";
                return false;
            }

            if (handleArray.Count == 0)
            {
                error = "handles must contain at least one item";
                return false;
            }

            if (handleArray.Count > MaxHandles)
            {
                error = $"handles must contain no more than {MaxHandles} items";
                return false;
            }

            var handles = new List<string>(handleArray.Count);
            for (var index = 0; index < handleArray.Count; index++)
            {
                var token = handleArray[index];
                if (token == null || token.Type != JTokenType.String)
                {
                    error = $"handles[{index}] must be a string";
                    return false;
                }

                var handle = token.Value<string>();
                if (!CadWire.TryParseHandleValue(handle, out _, out var handleError))
                {
                    error = $"handles[{index}]: {handleError}";
                    return false;
                }

                handles.Add(handle);
            }

            var includeGeometry = false;
            var includeGeometryToken = obj["include_geometry"];
            if (includeGeometryToken != null)
            {
                if (includeGeometryToken.Type != JTokenType.Boolean)
                {
                    error = "include_geometry must be a boolean";
                    return false;
                }

                includeGeometry = includeGeometryToken.Value<bool>();
            }

            request = new HandleBatchRequest(handles, includeGeometry);
            return true;
        }

        internal static bool TryParseCanonicalJson(
            string canonicalParametersJson,
            out HandleBatchRequest request,
            out string error)
        {
            request = null;
            error = null;

            if (string.IsNullOrWhiteSpace(canonicalParametersJson))
            {
                error = "params must be a valid JSON object";
                return false;
            }

            JObject obj;
            try
            {
                using (var stringReader = new StringReader(canonicalParametersJson))
                using (var jsonReader = new JsonTextReader(stringReader))
                {
                    obj = JObject.Load(jsonReader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        CommentHandling = CommentHandling.Ignore
                    });

                    while (jsonReader.Read())
                    {
                        if (jsonReader.TokenType != JsonToken.Comment)
                        {
                            error = "params must contain exactly one JSON object";
                            return false;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                error = "params must be a valid JSON object";
                return false;
            }

            var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "handles",
                "include_geometry"
            };
            var unsupportedProperty = obj.Properties()
                .Select(property => property.Name)
                .FirstOrDefault(name => !allowedProperties.Contains(name));
            if (unsupportedProperty != null)
            {
                error = $"params contains unsupported field '{unsupportedProperty}'";
                return false;
            }

            return TryCreate(obj, out request, out error);
        }
    }
}
