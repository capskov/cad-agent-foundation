using System.Text;
using System.Text.Json;

namespace Bimwright.Dwg.Plugin.Execution;

/// <summary>
/// Produces the canonical JSON object representation persisted by the plugin
/// journal. Callers that later compare an exact target must use this same form.
/// </summary>
public static class PluginJsonCanonicalizer
{
    public static string CanonicalizeObject(string json, string parameterName)
    {
        if (json is null)
            throw new ArgumentNullException(parameterName);

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The JSON value must be an object.", parameterName);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                JsonProperty[] properties = value.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                for (int index = 1; index < properties.Length; index++)
                    if (properties[index - 1].Name == properties[index].Name)
                        throw new ArgumentException("Duplicate JSON property names cannot be canonicalized.");
                writer.WriteStartObject();
                foreach (JsonProperty property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException("Undefined JSON cannot be canonicalized.");
        }
    }
}

/// <summary>
/// Creates the complete AutoCAD 2026 document target used by the plugin
/// registry. The shared journal canonicalizer fixes property order before the
/// target is bound, stored, submitted, or compared.
/// </summary>
public static class Acad26DocumentTargetJson
{
    public const string Schema = "bimwright-acad26-document-target-v1";

    public static string Create(
        string contextId,
        string sessionId,
        string databaseSessionId,
        string databaseFingerprint,
        string? drawingPath,
        string ownerId,
        int ownerProcessId,
        string ownerStartedAtUtc,
        string ownerGeneration)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("context_id", contextId);
            writer.WriteString("document_session_id", sessionId);
            writer.WriteString("database_session_id", databaseSessionId);
            writer.WriteString("database_fingerprint", databaseFingerprint);
            if (drawingPath is null)
            {
                writer.WriteNull("drawing_path");
                writer.WriteString("unsaved_session_id", sessionId);
            }
            else
            {
                writer.WriteString("drawing_path", drawingPath);
                writer.WriteNull("unsaved_session_id");
            }
            writer.WriteString("owner_id", ownerId);
            writer.WriteNumber("owner_pid", ownerProcessId);
            writer.WriteString("owner_started_at_utc", ownerStartedAtUtc);
            writer.WriteString("owner_generation", ownerGeneration);
            writer.WriteEndObject();
        }

        return PluginJsonCanonicalizer.CanonicalizeObject(
            Encoding.UTF8.GetString(buffer.ToArray()), nameof(Acad26DocumentTargetJson));
    }
}
