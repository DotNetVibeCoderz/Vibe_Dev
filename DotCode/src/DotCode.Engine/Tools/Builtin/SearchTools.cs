using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Util;
using Microsoft.Extensions.FileSystemGlobbing;

namespace DotCode.Engine.Tools.Builtin;

internal static class SearchCommon
{
    public static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", ".venv", "venv", "dist", "build", ".next", "target", ".gradle", "packages",
    };

    public static string? RipgrepPath => ProcessRunner.FindOnPath("rg");

    public static IEnumerable<string> EnumerateFiles(string root, bool includeSkipped = false)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch (Exception) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (includeSkipped || !SkipDirs.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }
}

public sealed class GlobTool : Tool
{
    public override string Name => "Glob";
    public override string Description => """
        Fast file pattern matching for any codebase size. Supports glob patterns like "**/*.cs" or "src/**/*.{ts,tsx}".
        Returns matching file paths sorted by modification time (newest first), up to 100 results.
        Skips .git, node_modules, bin, obj and other build folders unless the pattern names them.
        For open-ended searches that may need several rounds, use the Agent tool instead.
        """;
    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "pattern":{"type":"string","description":"Glob pattern to match files against"},
          "path":{"type":"string","description":"Directory to search in (defaults to the working directory)"}},
         "required":["pattern"]}
        """);
    public override bool IsReadOnly(JsonElement input) => true;
    public override string DisplayName(JsonElement input, AgentSession s) =>
        $"Search(pattern: \"{Str(input, "pattern")}\"{(input.GetString("path") is { } p ? $", path: \"{DotCodePaths.Display(DotCodePaths.Resolve(p, s.Cwd), s.Cwd)}\"" : "")})";
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) =>
        new(PermissionKind.ReadFile, input.GetString("path") is { } p ? DotCodePaths.Resolve(p, s.Cwd) : s.Cwd);

    public override Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var root = input.GetString("path") is { Length: > 0 } p ? DotCodePaths.Resolve(p, ctx.Cwd) : ctx.Cwd;
        if (!Directory.Exists(root)) return Task.FromResult(ToolResult.Error($"Directory does not exist: {root}"));
        var sw = Stopwatch.StartNew();
        var pattern = Str(input, "pattern").Replace('\\', '/');
        var patterns = ExpandBraces(pattern);
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var pat in patterns) matcher.AddInclude(pat.StartsWith("./", StringComparison.Ordinal) ? pat[2..] : pat);
        var includeSkipped = SearchCommon.SkipDirs.Any(d => pattern.Contains(d + "/", StringComparison.OrdinalIgnoreCase));

        var matches = new List<(string Path, DateTime Mtime)>();
        foreach (var file in SearchCommon.EnumerateFiles(root, includeSkipped))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (matcher.Match(rel).HasMatches)
            {
                DateTime m;
                try { m = File.GetLastWriteTimeUtc(file); } catch (IOException) { m = DateTime.MinValue; }
                matches.Add((file, m));
            }
        }
        var sorted = matches.OrderByDescending(m => m.Mtime).Select(m => m.Path).ToList();
        var shown = sorted.Take(100).ToList();
        var text = shown.Count == 0 ? "No files found" : string.Join('\n', shown) + (sorted.Count > 100 ? $"\n(Results are truncated: showing 100 of {sorted.Count}. Use a more specific path or pattern.)" : "");
        return Task.FromResult(ToolResult.Ok(text, $"Found {TextUtil.Plural(sorted.Count, "file")} in {sw.ElapsedMilliseconds}ms", display: ""));
    }

    /// <summary>Expands "{a,b}" alternatives (FileSystemGlobbing does not support braces).</summary>
    public static List<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{');
        var close = open >= 0 ? pattern.IndexOf('}', open) : -1;
        if (open < 0 || close < 0) return [pattern];
        var result = new List<string>();
        foreach (var alt in pattern[(open + 1)..close].Split(','))
            result.AddRange(ExpandBraces(pattern[..open] + alt + pattern[(close + 1)..]));
        return result;
    }
}

public sealed class GrepTool : Tool
{
    public override string Name => "Grep";
    public override string Description => """
        Content search built on ripgrep (with a managed fallback). Supports full regex syntax (e.g. "log.*Error", "function\s+\w+").
        - Filter files with glob (e.g. "*.cs", "*.{ts,tsx}") or type (e.g. "cs", "js", "py").
        - output_mode: "files_with_matches" (default, paths only), "content" (matching lines, supports -A/-B/-C and -n), "count".
        - Use -i for case-insensitive search, multiline for patterns spanning lines, head_limit to cap results.
        - ALWAYS use this tool for searching file contents instead of grep/rg in the shell.
        """;
    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "pattern":{"type":"string","description":"Regular expression to search for"},
          "path":{"type":"string","description":"File or directory to search (defaults to the working directory)"},
          "glob":{"type":"string","description":"Glob filter for files, e.g. *.js"},
          "type":{"type":"string","description":"File type filter (rg --type), e.g. js, py, cs"},
          "output_mode":{"type":"string","enum":["content","files_with_matches","count"]},
          "-i":{"type":"boolean","description":"Case insensitive"},
          "-n":{"type":"boolean","description":"Show line numbers (content mode, default true)"},
          "-A":{"type":"number","description":"Lines after each match"},
          "-B":{"type":"number","description":"Lines before each match"},
          "-C":{"type":"number","description":"Lines before and after each match"},
          "multiline":{"type":"boolean","description":"Allow patterns to span lines"},
          "head_limit":{"type":"number","description":"Limit output to the first N lines/entries"}},
         "required":["pattern"]}
        """);
    public override bool IsReadOnly(JsonElement input) => true;
    public override int MaxResultChars => 40_000;
    public override string DisplayName(JsonElement input, AgentSession s) =>
        $"Search(pattern: \"{Str(input, "pattern")}\"{(input.GetString("path") is { } p ? $", path: \"{DotCodePaths.Display(DotCodePaths.Resolve(p, s.Cwd), s.Cwd)}\"" : "")})";
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) =>
        new(PermissionKind.ReadFile, input.GetString("path") is { } p ? DotCodePaths.Resolve(p, s.Cwd) : s.Cwd);

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var root = input.GetString("path") is { Length: > 0 } p ? DotCodePaths.Resolve(p, ctx.Cwd) : ctx.Cwd;
        if (!Directory.Exists(root) && !File.Exists(root)) return ToolResult.Error($"Path does not exist: {root}");
        var mode = input.GetString("output_mode") ?? "files_with_matches";
        var headLimit = (int?)input.GetProp("head_limit")?.GetDouble() ?? 0;

        string output;
        if (SearchCommon.RipgrepPath is { } rg) output = await RunRipgrep(rg, input, root, mode, ct).ConfigureAwait(false);
        else output = ManagedSearch(input, root, mode, ct);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (mode == "files_with_matches")
        {
            // Newest first, like Glob.
            lines = [.. lines.OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })];
        }
        var total = lines.Count;
        if (headLimit > 0 && lines.Count > headLimit) lines = lines.Take(headLimit).ToList();
        var text = lines.Count == 0 ? "No matches found" : string.Join('\n', lines);
        var summary = mode switch
        {
            "content" => $"Found {TextUtil.Plural(total, "line")}",
            "count" => $"Found {TextUtil.Plural(lines.Sum(l => int.TryParse(l[(l.LastIndexOf(':') + 1)..], out var n) ? n : 0), "match", "matches")}",
            _ => $"Found {TextUtil.Plural(total, "file")}",
        };
        if (mode == "files_with_matches" && total > 0) text = $"Found {total} files\n{text}";
        return ToolResult.Ok(text, summary, display: "");
    }

    private static async Task<string> RunRipgrep(string rg, JsonElement input, string root, string mode, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(rg) { WorkingDirectory = Directory.Exists(root) ? root : Path.GetDirectoryName(root)! };
        var args = psi.ArgumentList;
        args.Add("--no-config");
        args.Add("--hidden");
        args.Add("--glob"); args.Add("!.git");
        args.Add("--max-columns"); args.Add("500");
        switch (mode)
        {
            case "files_with_matches": args.Add("-l"); break;
            case "count": args.Add("-c"); break;
            default:
                if (input.GetBool("-n") != false) args.Add("-n");
                foreach (var flag in new[] { "-A", "-B", "-C" })
                    if (input.GetProp(flag) is { } v) { args.Add(flag); args.Add(((int)v.GetDouble()).ToString()); }
                break;
        }
        if (input.GetBool("-i") == true) args.Add("-i");
        if (input.GetBool("multiline") == true) { args.Add("-U"); args.Add("--multiline-dotall"); }
        if (input.GetString("glob") is { Length: > 0 } glob) { args.Add("--glob"); args.Add(glob); }
        if (input.GetString("type") is { Length: > 0 } type) { args.Add("--type"); args.Add(type); }
        args.Add("-e");
        args.Add(Str(input, "pattern"));
        args.Add(root);
        var result = await ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(60), ct, maxOutputChars: 500_000).ConfigureAwait(false);
        return result.Output;
    }

    private static string ManagedSearch(JsonElement input, string root, string mode, CancellationToken ct)
    {
        var options = RegexOptions.Compiled | (input.GetBool("-i") == true ? RegexOptions.IgnoreCase : 0) | (input.GetBool("multiline") == true ? RegexOptions.Singleline : 0);
        Regex regex;
        try { regex = new Regex(Str(input, "pattern"), options, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { return $"Invalid regex: {ex.Message}"; }
        Matcher? matcher = null;
        if (input.GetString("glob") is { Length: > 0 } glob)
        {
            matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var g in GlobTool.ExpandBraces(glob)) matcher.AddInclude(g.Contains('/') ? g : "**/" + g);
        }
        var typeExt = input.GetString("type") is { Length: > 0 } t ? "." + t : null;
        int after = (int?)input.GetProp("-A")?.GetDouble() ?? (int?)input.GetProp("-C")?.GetDouble() ?? 0;
        int before = (int?)input.GetProp("-B")?.GetDouble() ?? (int?)input.GetProp("-C")?.GetDouble() ?? 0;

        var sb = new StringBuilder();
        var files = File.Exists(root) ? [root] : SearchCommon.EnumerateFiles(root);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(Directory.Exists(root) ? root : Path.GetDirectoryName(root)!, file).Replace('\\', '/');
            if (matcher is not null && !matcher.Match(rel).HasMatches) continue;
            if (typeExt is not null && !file.EndsWith(typeExt, StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 5_000_000) continue;
                var bytes = File.ReadAllBytes(file);
                if (TextUtil.LooksBinary(bytes)) continue;
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception) { continue; }

            if (mode == "files_with_matches") { if (regex.IsMatch(text)) sb.Append(file).Append('\n'); continue; }
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (mode == "count")
            {
                var c = lines.Count(l => regex.IsMatch(l));
                if (c > 0) sb.Append(file).Append(':').Append(c).Append('\n');
                continue;
            }
            var printed = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                for (var k = Math.Max(Math.Max(0, i - before), printed + 1); k <= Math.Min(lines.Length - 1, i + after); k++)
                {
                    sb.Append(file).Append(k == i ? ':' : '-').Append(k + 1).Append(k == i ? ':' : '-').Append(lines[k]).Append('\n');
                    printed = k;
                }
            }
        }
        return sb.ToString();
    }
}
