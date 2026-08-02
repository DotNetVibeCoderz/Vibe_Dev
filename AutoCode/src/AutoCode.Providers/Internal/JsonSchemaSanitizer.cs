// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoCode.Providers.Internal;

/// <summary>
/// Rewrites a JSON Schema into the restricted OpenAPI subset that Gemini accepts.
///
/// EN: Gemini rejects <c>$schema</c>, <c>additionalProperties</c>, <c>const</c>, <c>oneOf</c> and friends,
/// and it wants <c>type</c> as a single string rather than a union. Passing an untouched schema through
/// produces a 400, so every declaration is normalised here first.
/// ID: Gemini menolak kata kunci schema tertentu, sehingga skema dinormalisasi terlebih dulu di sini.
/// </summary>
internal static class JsonSchemaSanitizer
{
    private static readonly HashSet<string> Unsupported = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$ref", "$defs", "definitions", "additionalProperties",
        "const", "oneOf", "allOf", "not", "if", "then", "else",
        "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "patternProperties", "propertyNames", "unevaluatedProperties",
        "minContains", "maxContains", "dependentRequired", "examples", "default",
    };

    public static JsonNode? ForGemini(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        var cleaned = Clean(node);

        // Gemini requires an object schema with at least an empty property bag.
        if (cleaned is JsonObject obj)
        {
            obj["type"] ??= "object";
            if (obj["type"]?.GetValue<string>() == "object" && obj["properties"] is null)
                obj["properties"] = new JsonObject();
        }

        return cleaned;
    }

    private static JsonNode? Clean(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var result = new JsonObject();

                    foreach (var (key, value) in obj)
                    {
                        if (Unsupported.Contains(key))
                            continue;

                        if (key == "type" && value is JsonArray union)
                        {
                            // "type": ["string", "null"]  ->  "type": "string", "nullable": true
                            var first = union.FirstOrDefault(v => v?.GetValue<string>() != "null");
                            result["type"] = first?.GetValue<string>() ?? "string";
                            if (union.Any(v => v?.GetValue<string>() == "null"))
                                result["nullable"] = true;
                            continue;
                        }

                        result[key] = Clean(value?.DeepClone());
                    }

                    return result;
                }

            case JsonArray array:
                {
                    var result = new JsonArray();
                    foreach (var item in array)
                        result.Add(Clean(item?.DeepClone()));
                    return result;
                }

            default:
                return node;
        }
    }
}
