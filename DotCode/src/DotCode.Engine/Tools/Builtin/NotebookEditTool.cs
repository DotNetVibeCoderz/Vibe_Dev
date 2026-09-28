using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotCode.Abstractions;
using DotCode.Engine.Agent;

namespace DotCode.Engine.Tools.Builtin;

/// <summary>Edits Jupyter notebook cells (replace, insert, delete) by cell id or index.</summary>
public sealed class NotebookEditTool : Tool
{
    public override string Name => "NotebookEdit";
    public override string Description => "Replaces, inserts or deletes a cell in a Jupyter notebook (.ipynb). Identify the cell by cell_id (or a 0-based index as cell_id). Read the notebook first. edit_mode: replace (default) | insert (after cell_id, or at start) | delete.";
    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "notebook_path":{"type":"string","description":"Absolute path to the .ipynb file"},
          "cell_id":{"type":"string","description":"Cell id or 0-based index"},
          "new_source":{"type":"string","description":"New cell source"},
          "cell_type":{"type":"string","enum":["code","markdown"]},
          "edit_mode":{"type":"string","enum":["replace","insert","delete"]}},
         "required":["notebook_path","new_source"]}
        """);

    private static string NbPath(JsonElement input, AgentSession s) => DotCodePaths.Resolve(Str(input, "notebook_path"), s.Cwd);
    public override string DisplayName(JsonElement input, AgentSession s) => $"NotebookEdit({DotCodePaths.Display(NbPath(input, s), s.Cwd)})";
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.EditFile, NbPath(input, s));
    public override string? Validate(JsonElement input, AgentSession s)
    {
        var path = NbPath(input, s);
        if (!path.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase)) return "notebook_path must be a .ipynb file";
        if (!File.Exists(path)) return $"Notebook does not exist: {path}";
        return s.FileState.WasRead(path) ? null : "Notebook has not been read yet. Read it first before editing.";
    }

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = NbPath(input, ctx.Session);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) as JsonObject;
        if (root?["cells"] is not JsonArray cells) return ToolResult.Error("Invalid notebook: no cells array");
        var mode = input.GetString("edit_mode") ?? "replace";
        var id = input.GetString("cell_id");
        var index = -1;
        if (id is not null)
        {
            index = cells.Select((c, i) => (c, i)).FirstOrDefault(x => x.c?["id"]?.GetValue<string>() == id, (null, -1)).Item2;
            if (index < 0 && int.TryParse(id, out var n)) index = n;
            if (index < 0 || index >= cells.Count) return ToolResult.Error($"Cell not found: {id}");
        }
        var source = Str(input, "new_source");
        ctx.Session.Checkpoints.BeforeModify(path);
        switch (mode)
        {
            case "delete":
                if (index < 0) return ToolResult.Error("cell_id is required for delete");
                cells.RemoveAt(index);
                break;
            case "insert":
                var type = input.GetString("cell_type") ?? "code";
                var cell = new JsonObject
                {
                    ["cell_type"] = type,
                    ["id"] = Guid.NewGuid().ToString("n")[..8],
                    ["metadata"] = new JsonObject(),
                    ["source"] = SourceArray(source),
                };
                if (type == "code") { cell["outputs"] = new JsonArray(); cell["execution_count"] = null; }
                cells.Insert(index + 1, cell);
                break;
            default:
                if (index < 0) return ToolResult.Error("cell_id is required for replace");
                if (cells[index] is JsonObject target)
                {
                    target["source"] = SourceArray(source);
                    if (input.GetString("cell_type") is { } ct2) target["cell_type"] = ct2;
                    if (target["cell_type"]?.GetValue<string>() == "code") { target["outputs"] = new JsonArray(); target["execution_count"] = null; }
                }
                break;
        }
        await File.WriteAllTextAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        ctx.Session.FileState.MarkRead(path);
        return ToolResult.Ok($"Notebook {path} updated ({mode} cell {id ?? "0"}).", $"{char.ToUpperInvariant(mode[0])}{mode[1..]}d cell");
    }

    private static JsonArray SourceArray(string source)
    {
        var arr = new JsonArray();
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++) arr.Add((JsonNode?)JsonValue.Create(i < lines.Length - 1 ? lines[i] + "\n" : lines[i]));
        return arr;
    }

    public static string Render(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root?["cells"] is not JsonArray cells) return json;
            var sb = new StringBuilder();
            for (var i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                sb.Append($"<cell id=\"{c?["id"]?.GetValue<string>() ?? i.ToString()}\" index=\"{i}\" type=\"{c?["cell_type"]?.GetValue<string>()}\">\n");
                sb.Append(Join(c?["source"])).Append('\n');
                if (c?["outputs"] is JsonArray outputs)
                    foreach (var o in outputs)
                    {
                        var text = Join(o?["text"]) + Join(o?["data"]?["text/plain"]);
                        if (text.Length > 0) sb.Append("<output>\n").Append(text.Length > 5000 ? text[..5000] + "…" : text).Append("\n</output>\n");
                    }
                sb.Append("</cell>\n");
            }
            return sb.ToString();
        }
        catch (JsonException) { return json; }
    }

    private static string Join(JsonNode? node) => node switch
    {
        JsonArray a => string.Concat(a.Select(x => x?.GetValue<string>())),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => "",
    };
}
