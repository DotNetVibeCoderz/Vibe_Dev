using System.Text;
using System.Text.RegularExpressions;
using Marbots.Abstractions;

namespace Marbots.Kernel;

public sealed class ReadFileFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "read_file", "Read a UTF-8 text file from the workspace. Supports optional line offset/limit.",
        Schema(("path", "string", "Path relative to the workspace root", true),
               ("offset", "integer", "1-based line to start from", false),
               ("limit", "integer", "Maximum number of lines (default 400)", false)),
        "files", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var path = WorkspacePaths.Resolve(ctx.WorkspacePath, call.Require("path"));
        if (!File.Exists(path)) return FunctionResult.Fail($"File not found: {call.GetString("path")}");
        var offset = Math.Max(1, call.GetInt("offset", 1));
        var limit = Math.Clamp(call.GetInt("limit", 400), 1, 4000);
        var sb = new StringBuilder();
        var line = 0;
        using var reader = new StreamReader(path, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } text)
        {
            line++;
            if (line < offset) continue;
            if (line >= offset + limit) { sb.Append("…[more lines available, use offset]"); break; }
            sb.Append(line).Append('\t').AppendLine(text);
        }
        return FunctionResult.Ok(Truncate(sb.Length == 0 ? "(empty file)" : sb.ToString(), 60_000));
    }
}

public sealed class WriteFileFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "write_file", "Create or overwrite a text file in the workspace. Parent folders are created automatically.",
        Schema(("path", "string", "Path relative to the workspace root", true),
               ("content", "string", "Full file content", true)),
        "files", PermissionCategory.WorkspaceWrite, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var rel = call.Require("path");
        var path = WorkspacePaths.Resolve(ctx.WorkspacePath, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = call.GetString("content") ?? "";
        await File.WriteAllTextAsync(path, content, ct);
        return FunctionResult.Ok($"Wrote {Encoding.UTF8.GetByteCount(content)} bytes to {WorkspacePaths.ToRelative(ctx.WorkspacePath, path)}");
    }
}

public sealed class EditFileFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "edit_file", "Replace an exact text fragment in a workspace file. old_text must appear exactly once unless replace_all is true.",
        Schema(("path", "string", "Path relative to the workspace root", true),
               ("old_text", "string", "Exact text to find", true),
               ("new_text", "string", "Replacement text", true),
               ("replace_all", "boolean", "Replace every occurrence", false)),
        "files", PermissionCategory.WorkspaceWrite, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var path = WorkspacePaths.Resolve(ctx.WorkspacePath, call.Require("path"));
        if (!File.Exists(path)) return FunctionResult.Fail("File not found.");
        var oldText = call.Require("old_text");
        var newText = call.GetString("new_text") ?? "";
        var text = await File.ReadAllTextAsync(path, ct);
        var count = CountOccurrences(text, oldText);
        if (count == 0) return FunctionResult.Fail("old_text was not found in the file.");
        if (count > 1 && !call.GetBool("replace_all", false)) return FunctionResult.Fail($"old_text matches {count} times; make it unique or set replace_all.");
        await File.WriteAllTextAsync(path, text.Replace(oldText, newText, StringComparison.Ordinal), ct);
        return FunctionResult.Ok($"Replaced {count} occurrence(s).");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}

public sealed class ListFilesFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "list_files", "List files in a workspace folder, or match a glob pattern such as **/*.cs.",
        Schema(("path", "string", "Folder relative to the workspace root (default root)", false),
               ("pattern", "string", "Glob pattern, e.g. *.md or **/*.py", false)),
        "files", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var dir = WorkspacePaths.Resolve(ctx.WorkspacePath, call.GetString("path"));
        if (!Directory.Exists(dir)) return ValueTask.FromResult(FunctionResult.Ok("(folder does not exist yet — the workspace is empty)"));
        var pattern = call.GetString("pattern");
        var regex = string.IsNullOrEmpty(pattern) ? null : GlobToRegex(pattern);
        var sb = new StringBuilder();
        var n = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = WorkspacePaths.ToRelative(dir, file);
            if (rel.StartsWith("node_modules/", StringComparison.Ordinal) || rel.Contains("/node_modules/", StringComparison.Ordinal) || rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
            if (regex is not null && !regex.IsMatch(rel)) continue;
            sb.Append(rel).Append("  (").Append(new FileInfo(file).Length).AppendLine(" bytes)");
            if (++n >= 500) { sb.AppendLine("…[truncated at 500 files]"); break; }
        }
        return ValueTask.FromResult(FunctionResult.Ok(n == 0 ? "(no files)" : sb.ToString()));
    }

    public static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        if (!glob.Contains('/')) sb.Append("(?:.*/)?");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') i++;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

public sealed class DeleteFileFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "delete_file", "Delete a file or empty folder from the workspace.",
        Schema(("path", "string", "Path relative to the workspace root", true)),
        "files", PermissionCategory.DestructiveFilesystem, RiskLevel.Medium);

    protected override ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var path = WorkspacePaths.Resolve(ctx.WorkspacePath, call.Require("path"));
        if (path.Equals(Path.GetFullPath(ctx.WorkspacePath), StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(FunctionResult.Fail("Refusing to delete the workspace root."));
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
        else return ValueTask.FromResult(FunctionResult.Fail("Not found."));
        return ValueTask.FromResult(FunctionResult.Ok("Deleted."));
    }
}

public sealed class GrepFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "grep", "Search workspace files with a regular expression. Returns file:line: text matches.",
        Schema(("pattern", "string", ".NET regular expression", true),
               ("path", "string", "Folder to search (default workspace root)", false),
               ("glob", "string", "Optional file glob filter, e.g. *.cs", false)),
        "search", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var dir = WorkspacePaths.Resolve(ctx.WorkspacePath, call.GetString("path"));
        if (!Directory.Exists(dir)) return FunctionResult.Ok("(no files)");
        var pattern = call.Require("pattern");
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        // Whole-file pre-check, unless anchors or lookbehind could behave differently across line breaks.
        var wholeFile = pattern.Contains('$') || pattern.Contains("(?<", StringComparison.Ordinal) || pattern.Contains(@"\A", StringComparison.Ordinal) || pattern.Contains(@"\z", StringComparison.OrdinalIgnoreCase)
            ? null : new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromSeconds(5));
        var glob = call.GetString("glob") is { Length: > 0 } g ? ListFilesFunction.GlobToRegex(g) : null;
        // A plain literal is searched as UTF-8 bytes first, so files without it are never decoded.
        var literal = pattern.IndexOfAny(@"\^$.|?*+()[]{}".ToCharArray()) < 0 && pattern.Length > 0 ? Encoding.UTF8.GetBytes(pattern) : null;
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => (Path: f, Rel: WorkspacePaths.ToRelative(dir, f)))
            .Where(f => !f.Rel.Contains("node_modules/", StringComparison.Ordinal) && !f.Rel.StartsWith(".git/", StringComparison.Ordinal)
                        && (glob is null || glob.IsMatch(f.Rel)))
            .OrderBy(f => f.Rel, StringComparer.Ordinal) // same order on every OS (Linux does not enumerate sorted)
            .ToList();

        List<string> Search((string Path, string Rel) f)
        {
            var found = new List<string>();
            try
            {
                if (new FileInfo(f.Path).Length > 2_000_000) return found;
                var bytes = File.ReadAllBytes(f.Path);
                if (bytes.AsSpan().IndexOf((byte)0) >= 0) return found; // binary
                if (literal is not null && bytes.AsSpan().IndexOf(literal) < 0) return found;
                var content = Encoding.UTF8.GetString(bytes);
                // One scan of the whole file first: most files have no match (profiling: per-line matching dominated).
                if (wholeFile is not null && !wholeFile.IsMatch(content)) return found;
                var lineNo = 0;
                using var reader = new StringReader(content);
                while (reader.ReadLine() is { } line)
                {
                    lineNo++;
                    if (regex.IsMatch(line)) found.Add($"{f.Rel}:{lineNo}: {(line.Length > 300 ? line[..300] : line)}");
                    if (found.Count >= 200) break;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return found;
        }

        // Files are searched in parallel chunks; output keeps file order and stops at 200 matches.
        var sb = new StringBuilder();
        var hits = 0;
        const int chunk = 64;
        for (var start = 0; start < files.Count; start += chunk)
        {
            ct.ThrowIfCancellationRequested();
            var part = files.GetRange(start, Math.Min(chunk, files.Count - start));
            var results = new List<string>[part.Count];
            await Parallel.ForAsync(0, part.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                (i, _) => { results[i] = Search(part[i]); return ValueTask.CompletedTask; });
            foreach (var r in results)
                foreach (var line in r)
                {
                    sb.AppendLine(line);
                    if (++hits >= 200) return FunctionResult.Ok(sb.Append("…[truncated at 200 matches]").ToString());
                }
        }
        return FunctionResult.Ok(hits == 0 ? "No matches." : sb.ToString());
    }
}
