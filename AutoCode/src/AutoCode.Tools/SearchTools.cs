// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Utilities;

namespace AutoCode.Tools;

/// <summary>Finds files by glob, newest first.</summary>
public sealed class GlobTool : ToolBase
{
    public override string Name => "Glob";

    public override string Description =>
        """
        Fast file pattern matching. Supports patterns like "**/*.cs", "src/**/Program.cs" or "*.{json,yml}".

        - Returns paths sorted by modification time, newest first, so recent work surfaces immediately.
        - Build output and VCS directories (bin, obj, node_modules, .git, …) are skipped automatically.
        - Use this to locate files by name; use Grep to search their contents.
        """;

    public override ToolCapability Capability => ToolCapability.ReadsFiles;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Glob pattern to match file paths against." },
            "path": { "type": "string", "description": "Directory to search in. Defaults to the workspace root." },
            "limit": { "type": "integer", "description": "Maximum number of results. Defaults to 200." }
          },
          "required": ["pattern"]
        }
        """;

    public override string Summarize(JsonElement arguments) =>
        $"Glob({Peek(arguments, "pattern") ?? "?"})";

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var pattern = invocation.GetString("pattern");
        var root = invocation.TryGetString("path") is { Length: > 0 } p
            ? WorkspacePath.Resolve(invocation.WorkspaceRoot, p)
            : invocation.WorkspaceRoot;

        if (!Directory.Exists(root))
            return ValueTask.FromResult(ToolResult.Fail($"Directory not found: {root}"));

        var limit = Math.Clamp(invocation.TryGetInt("limit") ?? 200, 1, 5_000);

        var matches = FileWalker
            .Enumerate(root, cancellationToken)
            .Where(file => Glob.IsPathMatch(pattern, WorkspacePath.Relative(root, file)) ||
                           Glob.IsPathMatch(pattern, Path.GetFileName(file)))
            .Select(file => new FileInfo(file))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(limit)
            .Select(f => WorkspacePath.Relative(invocation.WorkspaceRoot, f.FullName))
            .ToList();

        if (matches.Count == 0)
            return ValueTask.FromResult(ToolResult.Ok($"No files matched '{pattern}' under {WorkspacePath.Relative(invocation.WorkspaceRoot, root)}."));

        return ValueTask.FromResult(ToolResult.Ok(
            string.Join('\n', matches),
            $"Glob({pattern}) → {matches.Count} file{(matches.Count == 1 ? "" : "s")}"));
    }
}

/// <summary>Regex content search across the workspace.</summary>
public sealed class GrepTool : ToolBase
{
    private const int MaxLineLength = 500;

    public override string Name => "Grep";

    public override string Description =>
        """
        Search file contents with a regular expression (.NET syntax).

        - output_mode: "content" (matching lines, default), "files_with_matches" (paths only), or "count".
        - Narrow the sweep with glob (e.g. "**/*.cs") and path; both are optional.
        - -i for case-insensitive, -n for line numbers (on by default in content mode),
          -A/-B/-C for trailing/leading/surrounding context lines.
        - Binary files and ignored directories are skipped.
        """;

    public override ToolCapability Capability => ToolCapability.ReadsFiles;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regular expression to search for." },
            "path": { "type": "string", "description": "File or directory to search. Defaults to the workspace root." },
            "glob": { "type": "string", "description": "Only search files whose path matches this glob." },
            "output_mode": { "type": "string", "enum": ["content", "files_with_matches", "count"] },
            "-i": { "type": "boolean", "description": "Case-insensitive search." },
            "-n": { "type": "boolean", "description": "Show line numbers. Defaults to true in content mode." },
            "-A": { "type": "integer", "description": "Lines of trailing context." },
            "-B": { "type": "integer", "description": "Lines of leading context." },
            "-C": { "type": "integer", "description": "Lines of context on both sides." },
            "head_limit": { "type": "integer", "description": "Cap on returned lines or paths. Defaults to 200." }
          },
          "required": ["pattern"]
        }
        """;

    public override string Summarize(JsonElement arguments) =>
        $"Grep({Peek(arguments, "pattern") ?? "?"})";

    protected override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var patternText = invocation.GetString("pattern");
        var caseInsensitive = invocation.TryGetBool("-i") ?? false;

        Regex regex;
        try
        {
            var regexOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (caseInsensitive) regexOptions |= RegexOptions.IgnoreCase;
            regex = new Regex(patternText, regexOptions, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail($"Invalid regular expression: {ex.Message}");
        }

        var target = invocation.TryGetString("path") is { Length: > 0 } p
            ? WorkspacePath.Resolve(invocation.WorkspaceRoot, p)
            : invocation.WorkspaceRoot;

        var globFilter = invocation.TryGetString("glob");
        var mode = invocation.TryGetString("output_mode") ?? "content";
        var headLimit = Math.Clamp(invocation.TryGetInt("head_limit") ?? 200, 1, 10_000);
        var showLineNumbers = invocation.TryGetBool("-n") ?? true;

        var context = invocation.TryGetInt("-C") ?? 0;
        var before = Math.Max(invocation.TryGetInt("-B") ?? 0, context);
        var after = Math.Max(invocation.TryGetInt("-A") ?? 0, context);

        var files = File.Exists(target)
            ? [target]
            : FileWalker.Enumerate(target, cancellationToken)
                .Where(f => globFilter is null ||
                            Glob.IsPathMatch(globFilter, WorkspacePath.Relative(invocation.WorkspaceRoot, f)) ||
                            Glob.IsPathMatch(globFilter, Path.GetFileName(f)))
                .ToList();

        var output = new StringBuilder();
        var matchedFiles = 0;
        var emitted = 0;
        var totalMatches = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (emitted >= headLimit)
                break;

            string[] lines;
            try
            {
                var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                if (LooksBinary(text))
                    continue;

                lines = text.Split('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var hits = new List<int>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                    hits.Add(i);
            }

            if (hits.Count == 0)
                continue;

            matchedFiles++;
            totalMatches += hits.Count;
            var relative = WorkspacePath.Relative(invocation.WorkspaceRoot, file);

            switch (mode)
            {
                case "files_with_matches":
                    output.Append(relative).Append('\n');
                    emitted++;
                    break;

                case "count":
                    output.Append(relative).Append(':').Append(hits.Count).Append('\n');
                    emitted++;
                    break;

                default:
                    {
                        var printed = new HashSet<int>();
                        foreach (var hit in hits)
                        {
                            if (emitted >= headLimit)
                                break;

                            var start = Math.Max(0, hit - before);
                            var end = Math.Min(lines.Length - 1, hit + after);

                            for (var i = start; i <= end && emitted < headLimit; i++)
                            {
                                if (!printed.Add(i))
                                    continue;

                                var line = lines[i].TrimEnd('\r');
                                if (line.Length > MaxLineLength)
                                    line = line[..MaxLineLength] + "…";

                                output.Append(relative);
                                if (showLineNumbers)
                                    output.Append(':').Append(i + 1);
                                output.Append(i == hit ? ':' : '-').Append(line).Append('\n');
                                emitted++;
                            }
                        }

                        break;
                    }
            }
        }

        if (matchedFiles == 0)
            return ToolResult.Ok($"No matches for /{patternText}/.");

        if (emitted >= headLimit)
            output.Append($"\n… output truncated at {headLimit} lines. Narrow the pattern or raise head_limit.\n");

        return ToolResult.Ok(
            output.ToString(),
            $"Grep({patternText}) → {totalMatches} match{(totalMatches == 1 ? "" : "es")} in {matchedFiles} file{(matchedFiles == 1 ? "" : "s")}");
    }

    /// <summary>Cheap binary sniff: a NUL byte in the first kilobyte.</summary>
    private static bool LooksBinary(string text)
    {
        var span = text.AsSpan(0, Math.Min(text.Length, 1024));
        return span.IndexOf('\0') >= 0;
    }
}

/// <summary>Lists a directory.</summary>
public sealed class ListTool : ToolBase
{
    public override string Name => "List";

    public override string Description =>
        """
        List the entries of a directory, directories first.

        - Ignored build/VCS directories are marked but not descended into.
        - Use Glob when you know part of a filename; this tool is for exploring an unfamiliar tree.
        """;

    public override ToolCapability Capability => ToolCapability.ReadsFiles;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Directory to list. Defaults to the workspace root." },
            "depth": { "type": "integer", "description": "Recursion depth, 1 (default) to 5." }
          }
        }
        """;

    public override string Summarize(JsonElement arguments) =>
        $"List({Peek(arguments, "path") ?? "."})";

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var root = invocation.TryGetString("path") is { Length: > 0 } p
            ? WorkspacePath.Resolve(invocation.WorkspaceRoot, p)
            : invocation.WorkspaceRoot;

        if (!Directory.Exists(root))
            return ValueTask.FromResult(ToolResult.Fail($"Directory not found: {root}"));

        var depth = Math.Clamp(invocation.TryGetInt("depth") ?? 1, 1, 5);
        var output = new StringBuilder();
        var count = 0;

        Render(root, "", depth, output, ref count, cancellationToken);

        return ValueTask.FromResult(ToolResult.Ok(
            $"{WorkspacePath.Relative(invocation.WorkspaceRoot, root)}/\n{output}",
            $"List({WorkspacePath.Relative(invocation.WorkspaceRoot, root)}) → {count} entries"));
    }

    private static void Render(
        string directory,
        string indent,
        int remainingDepth,
        StringBuilder output,
        ref int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> directories;
        IEnumerable<string> files;

        try
        {
            directories = Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
            files = Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in directories)
        {
            var name = Path.GetFileName(child);
            var ignored = WorkspacePath.IgnoredDirectories.Contains(name);

            output.Append(indent).Append("  ").Append(name).Append('/');
            if (ignored) output.Append("  (skipped)");
            output.Append('\n');
            count++;

            if (!ignored && remainingDepth > 1)
                Render(child, indent + "  ", remainingDepth - 1, output, ref count, cancellationToken);
        }

        foreach (var file in files)
        {
            output.Append(indent).Append("  ").Append(Path.GetFileName(file)).Append('\n');
            count++;
        }
    }
}

/// <summary>Shared recursive file enumeration that honours the ignore list.</summary>
internal static class FileWalker
{
    public static IEnumerable<string> Enumerate(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            foreach (var directory in directories)
            {
                if (!WorkspacePath.IgnoredDirectories.Contains(Path.GetFileName(directory)))
                    pending.Push(directory);
            }
        }
    }
}
