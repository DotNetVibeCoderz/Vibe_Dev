using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Lsp;
using DotCode.Engine.Util;

namespace DotCode.Engine.Tools.Builtin;

/// <summary>Code intelligence through language servers: definitions, references, implementations, hover,
/// document and workspace symbols, and diagnostics. Servers start on first use.</summary>
public sealed class LspTool : Tool
{
    public override string Name => "LSP";

    public override string Description => """
        Code intelligence from the project's language server (TypeScript/JavaScript, Python, Go, Rust, C#, C/C++, Java… whichever are installed).
        Operations:
        - goToDefinition / goToImplementation: where the symbol at file_path:line:character is defined / implemented
        - findReferences: every usage of that symbol across the workspace
        - hover: type signature and documentation of that symbol
        - documentSymbol: outline of a file (classes, functions, fields with line numbers)
        - workspaceSymbol: find symbols by name across the project (query)
        - diagnostics: compiler/type-checker errors and warnings for a file
        line and character are 1-based (as shown by Read). Prefer this over Grep when you need precise, semantic answers
        (e.g. references of an overloaded method, the type of a variable, errors after a change).
        """;

    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "operation":{"type":"string","enum":["goToDefinition","goToImplementation","findReferences","hover","documentSymbol","workspaceSymbol","diagnostics"]},
          "file_path":{"type":"string","description":"Path to the file (for workspaceSymbol: any file of the language to search)"},
          "line":{"type":"integer","description":"1-based line of the symbol"},
          "character":{"type":"integer","description":"1-based column of the symbol"},
          "query":{"type":"string","description":"Symbol name to search for (workspaceSymbol)"}},
         "required":["operation","file_path"]}
        """);

    public override bool IsReadOnly(JsonElement input) => true;
    public override bool IsEnabled(AgentSession session) => session.Runtime.Lsp.AnyAvailable;
    public override int MaxResultChars => 40_000;

    private static string FilePath(JsonElement input, AgentSession s) => DotCodePaths.Resolve(Str(input, "file_path"), s.Cwd);

    public override string DisplayName(JsonElement input, AgentSession s)
    {
        var op = Str(input, "operation");
        var target = op == "workspaceSymbol" ? $"\"{Str(input, "query")}\"" : DotCodePaths.Display(FilePath(input, s), s.Cwd)
            + (input.GetInt("line") is { } l ? $":{l}" + (input.GetInt("character") is { } c ? $":{c}" : "") : "");
        return $"LSP({op} {target})";
    }

    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.ReadFile, FilePath(input, s));

    public override string? Validate(JsonElement input, AgentSession s)
    {
        var op = Str(input, "operation");
        if (op is "goToDefinition" or "goToImplementation" or "findReferences" or "hover" && (input.GetInt("line") is not > 0 || input.GetInt("character") is not > 0))
            return $"{op} needs line and character (1-based).";
        if (op == "workspaceSymbol" && Str(input, "query").Length == 0) return "workspaceSymbol needs a query.";
        if (!File.Exists(FilePath(input, s))) return $"File does not exist: {FilePath(input, s)}";
        return null;
    }

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var session = ctx.Session;
        var path = FilePath(input, session);
        var op = Str(input, "operation");
        LspClient client;
        LspServerDef def;
        try
        {
            ctx.Progress($"Starting {session.Runtime.Lsp.ServerFor(path)?.Name ?? "language"} server…");
            (client, def) = await session.Runtime.Lsp.GetClientAsync(path, ct).ConfigureAwait(false);
            var seq = client.DiagnosticsSeq(path);
            await client.SyncDocumentAsync(path, LspManager.LanguageId(path, def), ct).ConfigureAwait(false);
            // Right after start-up some servers (tsserver, rust-analyzer…) answer before the project is loaded and
            // return partial results. The first diagnostics for a file signal that analysis is ready.
            if (seq == 0 && op != "diagnostics" && !client.Supports("diagnosticProvider"))
            {
                ctx.Progress($"Waiting for the {def.Name} server to load the project…");
                await client.GetDiagnosticsAsync(path, 0, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
            var uri = LspClient.PathToUri(path);
            var line = (input.GetInt("line") ?? 1) - 1;
            var character = (input.GetInt("character") ?? 1) - 1;
            void Position(Utf8JsonWriter w)
            {
                w.WriteStartObject("textDocument"); w.WriteString("uri", uri); w.WriteEndObject();
                w.WriteStartObject("position"); w.WriteNumber("line", line); w.WriteNumber("character", character); w.WriteEndObject();
            }
            var cwd = session.Cwd;
            switch (op)
            {
                case "goToDefinition" or "goToImplementation":
                {
                    var result = await client.RequestAsync(op == "goToDefinition" ? "textDocument/definition" : "textDocument/implementation", Position, ct).ConfigureAwait(false);
                    var locations = Locations(result);
                    return locations.Count == 0
                        ? ToolResult.Ok($"No {(op == "goToDefinition" ? "definition" : "implementation")} found for the symbol at {Display(path, cwd)}:{line + 1}:{character + 1}.", "No results")
                        : ToolResult.Ok(FormatLocations(locations, cwd, 50), TextUtil.Plural(locations.Count, "location"));
                }
                case "findReferences":
                {
                    var result = await client.RequestAsync("textDocument/references", w =>
                    {
                        Position(w);
                        w.WriteStartObject("context"); w.WriteBoolean("includeDeclaration", true); w.WriteEndObject();
                    }, ct).ConfigureAwait(false);
                    var locations = Locations(result);
                    var files = locations.Select(l => l.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    return locations.Count == 0
                        ? ToolResult.Ok("No references found.", "No results")
                        : ToolResult.Ok($"Found {TextUtil.Plural(locations.Count, "reference")} in {TextUtil.Plural(files, "file")}:\n" + FormatLocations(locations, cwd, 200),
                            $"{TextUtil.Plural(locations.Count, "reference")} in {TextUtil.Plural(files, "file")}");
                }
                case "hover":
                {
                    var result = await client.RequestAsync("textDocument/hover", Position, ct).ConfigureAwait(false);
                    var text = HoverText(result);
                    if (string.IsNullOrWhiteSpace(text)) return ToolResult.Ok("No hover information at that position.", "No results");
                    var headline = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal)) ?? "";
                    return ToolResult.Ok(text.Trim(), TextUtil.Truncate(headline, 80));
                }
                case "documentSymbol":
                {
                    var result = await client.RequestAsync("textDocument/documentSymbol", w => { w.WriteStartObject("textDocument"); w.WriteString("uri", uri); w.WriteEndObject(); }, ct).ConfigureAwait(false);
                    var sb = new StringBuilder();
                    var count = 0;
                    if (result.ValueKind == JsonValueKind.Array)
                        foreach (var s in result.EnumerateArray()) WriteSymbol(sb, s, 0, ref count, cwd);
                    return count == 0 ? ToolResult.Ok("No symbols found.", "No results") : ToolResult.Ok(sb.ToString().TrimEnd(), TextUtil.Plural(count, "symbol"));
                }
                case "workspaceSymbol":
                {
                    var query = Str(input, "query");
                    var result = await client.RequestAsync("workspace/symbol", w => w.WriteString("query", query), ct).ConfigureAwait(false);
                    var sb = new StringBuilder();
                    var count = 0;
                    if (result.ValueKind == JsonValueKind.Array)
                        foreach (var s in result.EnumerateArray().Take(200))
                        {
                            count++;
                            var loc = s.GetProp("location");
                            var p = loc?.GetString("uri") is { } u ? Display(LspClient.UriToPath(u), cwd) : "?";
                            var l = loc?.GetProp("range")?.GetProp("start")?.GetInt("line") is { } ln ? $":{ln + 1}" : "";
                            sb.Append(KindName(s.GetInt("kind"))).Append(' ').Append(s.GetString("name"))
                              .Append(s.GetString("containerName") is { Length: > 0 } c ? $" (in {c})" : "").Append(" — ").Append(p).Append(l).Append('\n');
                        }
                    return count == 0 ? ToolResult.Ok($"No symbols matching \"{query}\".", "No results") : ToolResult.Ok(sb.ToString().TrimEnd(), TextUtil.Plural(count, "symbol"));
                }
                case "diagnostics":
                {
                    var items = await client.GetDiagnosticsAsync(path, seq, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                    return items.Count == 0
                        ? ToolResult.Ok($"No diagnostics for {Display(path, cwd)}.", "No problems")
                        : ToolResult.Ok(FormatDiagnostics(path, items, cwd, 200), DiagnosticSummary(items));
                }
                default:
                    return ToolResult.Error($"Unknown operation: {op}");
            }
        }
        catch (LspException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    // ---------------- formatting

    public sealed record Location(string Path, int Line, int Character);

    private static List<Location> Locations(JsonElement result)
    {
        var list = new List<Location>();
        void Add(JsonElement l)
        {
            // Location {uri, range} or LocationLink {targetUri, targetSelectionRange}
            var uri = l.GetString("uri") ?? l.GetString("targetUri");
            var range = l.GetProp("range") ?? l.GetProp("targetSelectionRange") ?? l.GetProp("targetRange");
            if (uri is null) return;
            var start = range?.GetProp("start");
            list.Add(new Location(LspClient.UriToPath(uri), start?.GetInt("line") ?? 0, start?.GetInt("character") ?? 0));
        }
        if (result.ValueKind == JsonValueKind.Array) foreach (var l in result.EnumerateArray()) Add(l);
        else if (result.ValueKind == JsonValueKind.Object) Add(result);
        return list;
    }

    private static string Display(string path, string cwd) => DotCodePaths.Display(path, cwd).Replace('\\', '/');

    private static string FormatLocations(List<Location> locations, string cwd, int max)
    {
        var sb = new StringBuilder();
        var cache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in locations.Take(max))
        {
            sb.Append(Display(loc.Path, cwd)).Append(':').Append(loc.Line + 1).Append(':').Append(loc.Character + 1);
            if (!cache.TryGetValue(loc.Path, out var lines))
            {
                try { lines = File.Exists(loc.Path) ? File.ReadAllLines(loc.Path) : []; } catch (IOException) { lines = []; }
                cache[loc.Path] = lines;
            }
            if (loc.Line < lines.Length) sb.Append("  ").Append(TextUtil.Truncate(lines[loc.Line].Trim(), 160));
            sb.Append('\n');
        }
        if (locations.Count > max) sb.Append($"… {locations.Count - max} more\n");
        return sb.ToString().TrimEnd();
    }

    private static string HoverText(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || result.GetProp("contents") is not { } contents) return "";
        static string One(JsonElement c) => c.ValueKind switch
        {
            JsonValueKind.String => c.GetString() ?? "",
            JsonValueKind.Object => c.GetString("language") is { } lang ? $"```{lang}\n{c.GetString("value")}\n```" : c.GetString("value") ?? "",
            _ => "",
        };
        return contents.ValueKind == JsonValueKind.Array ? string.Join("\n\n", contents.EnumerateArray().Select(One)) : One(contents);
    }

    private static void WriteSymbol(StringBuilder sb, JsonElement s, int depth, ref int count, string cwd)
    {
        count++;
        // DocumentSymbol {name, kind, range, selectionRange, children} or SymbolInformation {name, kind, location}
        var range = s.GetProp("selectionRange") ?? s.GetProp("range") ?? s.GetProp("location")?.GetProp("range");
        var line = range?.GetProp("start")?.GetInt("line") is { } l ? l + 1 : 0;
        sb.Append(new string(' ', depth * 2)).Append(KindName(s.GetInt("kind"))).Append(' ').Append(s.GetString("name"));
        if (s.GetString("detail") is { Length: > 0 } detail) sb.Append(' ').Append(TextUtil.Truncate(detail.Replace('\n', ' '), 100));
        sb.Append(" — line ").Append(line).Append('\n');
        if (count > 500) return;
        if (s.GetProp("children") is { ValueKind: JsonValueKind.Array } children)
            foreach (var c in children.EnumerateArray()) WriteSymbol(sb, c, depth + 1, ref count, cwd);
    }

    private static readonly string[] Kinds =
    [
        "", "File", "Module", "Namespace", "Package", "Class", "Method", "Property", "Field", "Constructor", "Enum", "Interface",
        "Function", "Variable", "Constant", "String", "Number", "Boolean", "Array", "Object", "Key", "Null", "EnumMember", "Struct",
        "Event", "Operator", "TypeParameter",
    ];

    private static string KindName(int? kind) => kind is > 0 and < 27 ? Kinds[kind.Value] : "Symbol";

    private static string Severity(int s) => s switch { 1 => "error", 2 => "warning", 3 => "info", _ => "hint" };

    public static string FormatDiagnostics(string path, IReadOnlyList<LspDiagnostic> items, string cwd, int max)
    {
        var sb = new StringBuilder();
        foreach (var d in items.OrderBy(d => d.Severity).ThenBy(d => d.Line).Take(max))
        {
            sb.Append(Display(path, cwd)).Append(':').Append(d.Line + 1).Append(':').Append(d.Character + 1)
              .Append(" [").Append(Severity(d.Severity)).Append("] ").Append(d.Message.Replace("\r", "").Replace("\n", " "));
            if (d.Source is not null || d.Code is not null) sb.Append(" (").Append(string.Join(" ", new[] { d.Source, d.Code }.Where(x => x is not null))).Append(')');
            sb.Append('\n');
        }
        if (items.Count > max) sb.Append($"… {items.Count - max} more\n");
        return sb.ToString().TrimEnd();
    }

    private static string DiagnosticSummary(IReadOnlyList<LspDiagnostic> items)
    {
        var errors = items.Count(d => d.Severity == 1);
        var warnings = items.Count(d => d.Severity == 2);
        return string.Join(", ", new[] { errors > 0 ? TextUtil.Plural(errors, "error") : null, warnings > 0 ? TextUtil.Plural(warnings, "warning") : null, errors + warnings == 0 ? TextUtil.Plural(items.Count, "note") : null }.Where(x => x is not null));
    }

    /// <summary>After Edit/Write: when a language server is already running for the file, report the errors it now
    /// sees (never starts a server, waits at most a few seconds).</summary>
    public static async Task<string?> DiagnosticsAfterEditAsync(AgentSession session, string path, CancellationToken ct)
    {
        var lsp = session.Runtime.Lsp;
        if (!lsp.Enabled || !lsp.DiagnosticsAfterEdit || lsp.RunningClientFor(path) is not { } client || lsp.ServerFor(path) is not { } def) return null;
        try
        {
            var seq = client.DiagnosticsSeq(path);
            await client.SyncDocumentAsync(path, LspManager.LanguageId(path, def), ct).ConfigureAwait(false);
            var items = await client.GetDiagnosticsAsync(path, seq, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            var errors = items.Where(d => d.Severity == 1).ToList();
            if (errors.Count == 0) return null;
            return $"\n\n<new-diagnostics>\nThe {def.Name} language server reports {TextUtil.Plural(errors.Count, "error")} in this file after the change:\n"
                   + FormatDiagnostics(path, errors, session.Cwd, 20) + "\n</new-diagnostics>";
        }
        catch (Exception ex) when (ex is LspException or IOException) { return null; }
    }
}
