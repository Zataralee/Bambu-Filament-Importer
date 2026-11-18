using System.Text.Json;
using System.Text.Json.Nodes;

namespace BambuFilamentImporter.Services;

/// <summary>Reads Bambu JSON with last-property-wins semantics.</summary>
public static class BambuJson
{
    public static JsonNode? Parse(string json)
    {
        // Studio 2.9.1 ships PC/PVA profiles with repeated drying keys. JsonNode.Parse
        // accepts these initially but throws when its lazy object dictionary is used.
        // Materialize recursively so all readers/editors agree on the effective value.
        // JsonDocument still rejects malformed JSON; no catalog validation is skipped.
        using var document = JsonDocument.Parse(json);
        return Materialize(document.RootElement);
    }

    private static JsonNode? Materialize(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    result[property.Name] = Materialize(property.Value);
                }
                return result;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(Materialize(item));
                }
                return array;
            case JsonValueKind.Null:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }
}
