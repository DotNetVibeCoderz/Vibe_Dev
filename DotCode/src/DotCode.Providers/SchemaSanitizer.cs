using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotCode.Abstractions;

namespace DotCode.Providers;

/// <summary>Translates full JSON Schema tool definitions (built-in tools and MCP servers) into the dialect a
/// provider accepts. Results are cached per (schema, profile).</summary>
public static class SchemaSanitizer
{
    private static readonly ConcurrentDictionary<(string, JsonSchemaProfile), JsonElement> Cache = new();

    private static readonly HashSet<string> OpenApiAllowed =
    [
        "type", "format", "description", "nullable", "enum", "maxItems", "minItems", "properties", "required",
        "items", "minimum", "maximum", "minLength", "maxLength", "pattern", "anyOf", "propertyOrdering", "default", "title",
    ];

    public static JsonElement Sanitize(JsonElement schema, JsonSchemaProfile profile)
    {
        if (profile == JsonSchemaProfile.Full) return schema;
        var raw = schema.GetRawText();
        return Cache.GetOrAdd((raw, profile), static key =>
        {
            var node = JsonNode.Parse(key.Item1) ?? new JsonObject();
            var defs = (node as JsonObject)?["$defs"] as JsonObject ?? (node as JsonObject)?["definitions"] as JsonObject;
            var result = key.Item2 switch
            {
                JsonSchemaProfile.OpenApiSubset => ToOpenApi(node, defs, 0),
                JsonSchemaProfile.Strict => ToStrict(Inline(node, defs, 0)),
                JsonSchemaProfile.Minimal => ToMinimal(Inline(node, defs, 0), 0),
                _ => node,
            };
            if (result is JsonObject o && !o.ContainsKey("type")) o["type"] = "object";
            return DotCodeJson.Parse(result.ToJsonString());
        });
    }

    /// <summary>Resolves local <c>$ref</c> pointers (#/$defs/X) inline; depth-limited to break cycles.</summary>
    private static JsonNode Inline(JsonNode node, JsonObject? defs, int depth)
    {
        if (depth > 12) return new JsonObject { ["type"] = "object" };
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue refVal && refVal.TryGetValue<string>(out var r) && defs is not null)
                {
                    var name = r[(r.LastIndexOf('/') + 1)..];
                    if (defs[name] is { } target) return Inline(target.DeepClone(), defs, depth + 1);
                    return new JsonObject { ["type"] = "object" };
                }
                var copy = new JsonObject();
                foreach (var (k, v) in obj)
                {
                    if (k is "$defs" or "definitions" or "$schema" or "$id") continue;
                    copy[k] = v is null ? null : Inline(v, defs, depth + 1);
                }
                return copy;
            case JsonArray arr:
                var list = new JsonArray();
                foreach (var item in arr) list.Add(item is null ? null : Inline(item, defs, depth + 1));
                return list;
            default:
                return node.DeepClone();
        }
    }

    private static JsonNode ToOpenApi(JsonNode node, JsonObject? defs, int depth)
    {
        var inlined = Inline(node, defs, 0);
        return OpenApiWalk(inlined, depth);
    }

    private static JsonNode OpenApiWalk(JsonNode node, int depth)
    {
        if (node is not JsonObject obj) return node;
        var result = new JsonObject();

        // type: ["string","null"] -> type: string, nullable: true
        if (obj["type"] is JsonArray types)
        {
            var nonNull = types.Select(t => t?.GetValue<string>()).Where(t => t != "null").ToList();
            result["type"] = nonNull.FirstOrDefault() ?? "string";
            if (nonNull.Count < types.Count) result["nullable"] = true;
        }

        // anyOf/oneOf with a null branch -> nullable single schema; otherwise keep anyOf (supported) and drop oneOf/allOf by merging first.
        foreach (var key in new[] { "anyOf", "oneOf" })
        {
            if (obj[key] is not JsonArray variants) continue;
            var nonNull = variants.Where(v => !(v is JsonObject vo && vo["type"]?.GetValue<string>() == "null")).ToList();
            if (nonNull.Count == 1)
            {
                var single = OpenApiWalk(nonNull[0]!, depth + 1) as JsonObject ?? new JsonObject();
                foreach (var (k, v) in single) result[k] = v?.DeepClone();
                if (nonNull.Count < variants.Count) result["nullable"] = true;
            }
            else
            {
                var arr = new JsonArray();
                foreach (var v in nonNull) arr.Add(OpenApiWalk(v!, depth + 1));
                result["anyOf"] = arr;
            }
        }
        if (obj["allOf"] is JsonArray all)
        {
            foreach (var part in all)
                if (OpenApiWalk(part!, depth + 1) is JsonObject po)
                    foreach (var (k, v) in po) result[k] ??= v?.DeepClone();
        }

        foreach (var (k, v) in obj)
        {
            if (v is null || result.ContainsKey(k) || k is "anyOf" or "oneOf" or "allOf") continue;
            switch (k)
            {
                case "const":
                    result["enum"] = new JsonArray(v.DeepClone());
                    break;
                case "properties" when v is JsonObject props:
                    var p = new JsonObject();
                    foreach (var (pk, pv) in props) if (pv is not null) p[pk] = OpenApiWalk(pv, depth + 1);
                    result["properties"] = p;
                    break;
                case "items":
                    result["items"] = OpenApiWalk(v, depth + 1);
                    break;
                case "enum" when v is JsonArray e:
                    // Gemini requires string enums.
                    result["enum"] = new JsonArray(e.Select(x => (JsonNode?)JsonValue.Create(x?.ToString() ?? "")).ToArray());
                    if (!result.ContainsKey("type")) result["type"] = "string";
                    break;
                case "format" when v.GetValueKind() == JsonValueKind.String:
                    var f = v.GetValue<string>();
                    if (f is "enum" or "date-time") result["format"] = f;
                    break;
                default:
                    if (OpenApiAllowed.Contains(k)) result[k] = v.DeepClone();
                    break;
            }
        }
        if (result["enum"] is JsonArray && result["type"]?.GetValue<string>() is not "string") result["type"] = "string";
        if (result["type"]?.GetValue<string>() == "object" && result["properties"] is JsonObject { Count: 0 }) result.Remove("properties");
        if (result["required"] is JsonArray req && result["properties"] is JsonObject propsObj)
        {
            var filtered = new JsonArray(req.Where(x => x is not null && propsObj.ContainsKey(x.GetValue<string>())).Select(x => x!.DeepClone()).ToArray());
            if (filtered.Count == 0) result.Remove("required"); else result["required"] = filtered;
        }
        else if (result["properties"] is null) result.Remove("required");
        return result;
    }

    /// <summary>OpenAI strict mode: all properties required, additionalProperties false, optional -> nullable union.</summary>
    private static JsonNode ToStrict(JsonNode node)
    {
        if (node is not JsonObject obj) return node;
        if (obj["properties"] is JsonObject props)
        {
            var required = (obj["required"] as JsonArray)?.Select(x => x?.GetValue<string>()).ToHashSet() ?? [];
            var allNames = new JsonArray();
            foreach (var (name, schema) in props.ToList())
            {
                allNames.Add((JsonNode?)JsonValue.Create(name));
                var s = ToStrict(schema ?? new JsonObject());
                if (!required.Contains(name) && s is JsonObject so && so["type"] is JsonValue tv)
                    so["type"] = new JsonArray(tv.GetValue<string>(), "null");
                props[name] = s.Parent is null ? s : s.DeepClone();
            }
            obj["required"] = allNames;
            obj["additionalProperties"] = false;
        }
        if (obj["items"] is { } items) obj["items"] = ToStrict(items.DeepClone());
        obj.Remove("default");
        obj.Remove("format");
        return obj;
    }

    private static JsonNode ToMinimal(JsonNode node, int depth)
    {
        if (node is not JsonObject obj) return node;
        var result = new JsonObject();
        if (obj["type"] is { } t) result["type"] = t.DeepClone();
        if (obj["description"] is JsonValue d && d.TryGetValue<string>(out var desc) && depth <= 1)
            result["description"] = desc.Length > 200 ? desc[..200] : desc;
        if (obj["enum"] is { } en) result["enum"] = en.DeepClone();
        if (obj["required"] is { } rq) result["required"] = rq.DeepClone();
        if (obj["properties"] is JsonObject props && depth < 4)
        {
            var p = new JsonObject();
            foreach (var (k, v) in props) if (v is not null) p[k] = ToMinimal(v, depth + 1);
            result["properties"] = p;
        }
        if (obj["items"] is { } items && depth < 4) result["items"] = ToMinimal(items, depth + 1);
        return result;
    }
}
