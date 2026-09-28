using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Util;

namespace DotCode.Engine.Tools.Builtin;

public sealed class ReadTool : Tool
{
    public static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    private const int DefaultLimit = 2000;
    private const int MaxLineLength = 2000;
    private const long MaxBytesWithoutRange = 512 * 1024;

    public override string Name => "Read";
    public override string Description => """
        Reads a file from the local filesystem.
        - file_path should be absolute (relative paths are resolved against the working directory).
        - Reads up to 2000 lines from the start by default; use offset (1-based line) and limit for large files.
        - Lines longer than 2000 characters are truncated. Output uses `cat -n` format: line number, tab, content.
        - Reads images (PNG, JPG, GIF, WEBP) and returns them visually; reads PDFs as documents; renders Jupyter notebooks (.ipynb) cell by cell.
        - Read several files in parallel by calling this tool multiple times in one response.
        - Reading a directory is an error: use Glob or Bash ls instead.
        """;
    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "file_path":{"type":"string","description":"Absolute path to the file"},
          "offset":{"type":"integer","description":"1-based line number to start from"},
          "limit":{"type":"integer","description":"Number of lines to read"}},
         "required":["file_path"]}
        """);
    public override bool IsReadOnly(JsonElement input) => true;
    public override int MaxResultChars => 200_000;

    public override string DisplayName(JsonElement input, AgentSession s) => $"Read({DotCodePaths.Display(Path(input, s), s.Cwd)})";
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.ReadFile, Path(input, s));

    internal static string Path(JsonElement input, AgentSession s) => DotCodePaths.Resolve(Str(input, "file_path", Str(input, "path")), s.Cwd);

    public override Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = Path(input, ctx.Session);
        if (Directory.Exists(path)) return Task.FromResult(ToolResult.Error($"EISDIR: {path} is a directory. Use Glob or ls to list it."));
        if (!File.Exists(path))
        {
            var similar = FindSimilar(path);
            return Task.FromResult(ToolResult.Error($"File does not exist: {path}{(similar is null ? "" : $" Did you mean {similar}?")}"));
        }
        var ext = System.IO.Path.GetExtension(path);
        if (ImageTypes.TryGetValue(ext, out var media))
        {
            var bytes = File.ReadAllBytes(path);
            ctx.Session.FileState.MarkRead(path);
            return Task.FromResult(new ToolResult
            {
                Content = [new ImagePart(Convert.ToBase64String(bytes), media)],
                Summary = $"Read image ({bytes.Length / 1024.0:0.#}KB)",
            });
        }
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 20 * 1024 * 1024) return Task.FromResult(ToolResult.Error("PDF is larger than 20MB"));
            ctx.Session.FileState.MarkRead(path);
            return Task.FromResult(new ToolResult
            {
                Content = [new DocumentPart(Convert.ToBase64String(bytes), "application/pdf", System.IO.Path.GetFileName(path))],
                Summary = $"Read PDF ({bytes.Length / 1024.0:0.#}KB)",
            });
        }
        if (ext.Equals(".ipynb", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Session.FileState.MarkRead(path);
            var text = NotebookEditTool.Render(File.ReadAllText(path));
            return Task.FromResult(ToolResult.Ok(text, $"Read notebook ({text.Split('\n').Length} lines)"));
        }

        var info = new FileInfo(path);
        var offset = Math.Max(1, input.GetInt("offset") ?? 1);
        var limit = input.GetInt("limit") ?? DefaultLimit;
        if (info.Length > MaxBytesWithoutRange && input.GetInt("offset") is null && input.GetInt("limit") is null)
            return Task.FromResult(ToolResult.Error($"File content ({info.Length / 1024}KB) exceeds maximum allowed size (512KB). Use offset and limit to read specific portions, or Grep to search for specific content."));

        var head = new byte[Math.Min(8000, info.Length)];
        using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
        if (TextUtil.LooksBinary(head)) return Task.FromResult(ToolResult.Error($"{path} appears to be a binary file and cannot be displayed as text."));

        var content = File.ReadAllText(path);
        ctx.Session.FileState.MarkRead(path, partial: offset > 1 || input.GetInt("limit") is not null);
        if (content.Length == 0)
            return Task.FromResult(ToolResult.Ok("<system-reminder>Warning: the file exists but the contents are empty.</system-reminder>", "Read 0 lines"));
        var numbered = Number(content, offset, limit, out var count, out var total);
        if (count == 0)
            return Task.FromResult(ToolResult.Ok($"<system-reminder>Warning: the file exists but is shorter than the provided offset ({offset}). The file has {total} lines.</system-reminder>", "Read 0 lines"));
        return Task.FromResult(ToolResult.Ok(numbered, $"Read {TextUtil.Plural(count, "line")}", display: ""));
    }

    public static string Number(string content, int offset, int limit) => Number(content, offset, limit, out _, out _);

    public static string Number(string content, int offset, int limit, out int count, out int total)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        total = content.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        var sb = new StringBuilder();
        count = 0;
        for (var i = offset - 1; i < total && count < limit; i++, count++)
        {
            var line = lines[i];
            if (line.Length > MaxLineLength) line = line[..MaxLineLength] + "… [line truncated]";
            sb.Append((i + 1).ToString().PadLeft(6)).Append('\t').Append(line).Append('\n');
        }
        if (offset - 1 + count < total) sb.Append($"\n[{total - (offset - 1 + count)} more lines — use offset={offset + count} to continue]\n");
        return sb.ToString();
    }

    private static string? FindSimilar(string path)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (dir is null || !Directory.Exists(dir)) return null;
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            return Directory.EnumerateFiles(dir).FirstOrDefault(f => System.IO.Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return null; }
    }
}

public sealed class WriteTool : Tool
{
    public override string Name => "Write";
    public override string Description => """
        Writes a file to the local filesystem, creating parent directories as needed and overwriting any existing file.
        - If the file already exists you MUST Read it first, otherwise the call fails.
        - Prefer Edit for changes to existing files; use Write for new files or complete rewrites.
        - Do not create documentation or README files unless the user asked for them.
        """;
    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "file_path":{"type":"string","description":"Absolute path of the file to write"},
          "content":{"type":"string","description":"Full file content"}},
         "required":["file_path","content"]}
        """);

    public override string DisplayName(JsonElement input, AgentSession s) => $"Write({DotCodePaths.Display(ReadTool.Path(input, s), s.Cwd)})";
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.EditFile, ReadTool.Path(input, s));

    public override string? Validate(JsonElement input, AgentSession s)
    {
        var path = ReadTool.Path(input, s);
        if (Directory.Exists(path)) return $"{path} is a directory";
        if (File.Exists(path) && !s.FileState.WasRead(path)) return "File has not been read yet. Read it first before writing to it.";
        if (File.Exists(path) && s.FileState.ChangedSinceRead(path)) return "File has been modified since read, either by the user or by a linter. Read it again before attempting to write it.";
        return null;
    }

    public override (string Title, string? Detail, string? Diff) DescribeForPermission(JsonElement input, AgentSession s)
    {
        var path = ReadTool.Path(input, s);
        var exists = File.Exists(path);
        var diff = UnifiedDiff.Create(exists ? File.ReadAllText(path) : "", Str(input, "content"), DotCodePaths.Display(path, s.Cwd));
        return (exists ? $"Overwrite file {DotCodePaths.Display(path, s.Cwd)}" : $"Create file {DotCodePaths.Display(path, s.Cwd)}", null, diff);
    }

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ReadTool.Path(input, ctx.Session);
        var content = Str(input, "content");
        var exists = File.Exists(path);
        var old = exists ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : "";
        ctx.Session.Checkpoints.BeforeModify(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Preserve existing BOM and line endings when overwriting.
        var encoding = new UTF8Encoding(false);
        if (exists)
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            encoding = (UTF8Encoding)(TextUtil.DetectEncoding(bytes, out _) as UTF8Encoding ?? new UTF8Encoding(false));
            if (TextUtil.DetectNewline(old) == "\r\n" && !content.Contains("\r\n", StringComparison.Ordinal)) content = content.Replace("\n", "\r\n");
        }
        await File.WriteAllTextAsync(path, content, encoding, ct).ConfigureAwait(false);
        ctx.Session.FileState.MarkRead(path);
        var display = DotCodePaths.Display(path, ctx.Cwd);
        var diff = UnifiedDiff.Create(old, content, display);
        var lines = UnifiedDiff.SplitLines(content).Length;
        if (!exists)
            return ToolResult.Ok($"File created successfully at: {path}", $"Wrote {TextUtil.Plural(lines, "line")} to {display}", diff, display: content);
        var (add, rem) = UnifiedDiff.Count(diff);
        return ToolResult.Ok($"The file {path} has been overwritten successfully.", $"Updated {display} with {TextUtil.Plural(add, "addition")} and {TextUtil.Plural(rem, "removal")}", diff);
    }
}

public sealed class EditTool : Tool
{
    public override string Name => "Edit";
    public override string Description => """
        Performs exact string replacement in a file.
        - You must Read the file in this conversation before editing, otherwise the call fails.
        - old_string must match the file exactly, including whitespace and indentation (do not include the line-number prefix from Read output).
        - The edit fails if old_string is not unique; include more surrounding context or set replace_all to true.
        - Use replace_all to rename a variable or string throughout the file.
        - An empty old_string on a non-existent file creates it with new_string as content.
        """;
    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "file_path":{"type":"string","description":"Absolute path of the file to modify"},
          "old_string":{"type":"string","description":"Exact text to replace"},
          "new_string":{"type":"string","description":"Replacement text (must differ from old_string)"},
          "replace_all":{"type":"boolean","description":"Replace every occurrence (default false)"}},
         "required":["file_path","old_string","new_string"]}
        """);

    public override string DisplayName(JsonElement input, AgentSession s) => $"Update({DotCodePaths.Display(ReadTool.Path(input, s), s.Cwd)})";
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.EditFile, ReadTool.Path(input, s));

    public override string? Validate(JsonElement input, AgentSession s)
    {
        var path = ReadTool.Path(input, s);
        var oldString = Str(input, "old_string");
        var newString = Str(input, "new_string");
        if (oldString == newString) return "No changes to make: old_string and new_string are exactly the same.";
        if (!File.Exists(path)) return oldString.Length == 0 ? null : $"File does not exist: {path}";
        if (oldString.Length == 0) return "Cannot create new file - file already exists.";
        if (!s.FileState.WasRead(path)) return "File has not been read yet. Read it first before writing to it.";
        if (s.FileState.ChangedSinceRead(path)) return "File has been modified since read, either by the user or by a linter. Read it again before attempting to write it.";
        return null;
    }

    private static (string NewContent, int Count, string? Error) Apply(string content, string oldString, string newString, bool replaceAll)
    {
        var crlf = TextUtil.DetectNewline(content) == "\r\n";
        if (crlf)
        {
            // Model output uses \n; match against the file's CRLF form.
            if (!oldString.Contains("\r\n", StringComparison.Ordinal)) oldString = oldString.Replace("\n", "\r\n");
            if (!newString.Contains("\r\n", StringComparison.Ordinal)) newString = newString.Replace("\n", "\r\n");
        }
        var count = Occurrences(content, oldString);
        if (count == 0)
        {
            // Tolerate trailing-whitespace / quote-style drift: try a normalized match once.
            var alt = oldString.Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'');
            if (alt != oldString && Occurrences(content, alt) > 0) { oldString = alt; count = Occurrences(content, alt); }
        }
        if (count == 0) return (content, 0, "String to replace not found in file.");
        if (count > 1 && !replaceAll)
            return (content, count, $"Found {count} matches of the string to replace, but replace_all is false. To replace all occurrences, set replace_all to true. To replace only one occurrence, provide more context to uniquely identify the instance.");
        var result = replaceAll ? content.Replace(oldString, newString, StringComparison.Ordinal) : ReplaceFirst(content, oldString, newString);
        return (result, count, null);
    }

    private static int Occurrences(string text, string value)
    {
        if (value.Length == 0) return 0;
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var i = text.IndexOf(oldValue, StringComparison.Ordinal);
        return i < 0 ? text : string.Concat(text.AsSpan(0, i), newValue, text.AsSpan(i + oldValue.Length));
    }

    public override (string Title, string? Detail, string? Diff) DescribeForPermission(JsonElement input, AgentSession s)
    {
        var path = ReadTool.Path(input, s);
        var old = File.Exists(path) ? File.ReadAllText(path) : "";
        var (updated, _, _) = old.Length == 0 && Str(input, "old_string").Length == 0
            ? (Str(input, "new_string"), 1, (string?)null)
            : Apply(old, Str(input, "old_string"), Str(input, "new_string"), input.GetBool("replace_all") == true);
        return ($"Edit file {DotCodePaths.Display(path, s.Cwd)}", null, UnifiedDiff.Create(old, updated, DotCodePaths.Display(path, s.Cwd)));
    }

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ReadTool.Path(input, ctx.Session);
        var display = DotCodePaths.Display(path, ctx.Cwd);
        var oldString = Str(input, "old_string");
        var newString = Str(input, "new_string");
        ctx.Session.Checkpoints.BeforeModify(path);

        if (!File.Exists(path) && oldString.Length == 0)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, newString, new UTF8Encoding(false), ct).ConfigureAwait(false);
            ctx.Session.FileState.MarkRead(path);
            return ToolResult.Ok($"File created successfully at: {path}", $"Created {display}", UnifiedDiff.Create("", newString, display));
        }

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var encoding = TextUtil.DetectEncoding(bytes, out _);
        var content = encoding.GetString(bytes);
        if (content.Length > 0 && content[0] == '﻿') content = content[1..];
        var (updated, count, error) = Apply(content, oldString, newString, input.GetBool("replace_all") == true);
        if (error is not null) return ToolResult.Error(error);

        await File.WriteAllTextAsync(path, updated, encoding, ct).ConfigureAwait(false);
        ctx.Session.FileState.MarkRead(path);
        var diff = UnifiedDiff.Create(content, updated, display);
        var (add, rem) = UnifiedDiff.Count(diff);
        var summary = $"Updated {display} with {TextUtil.Plural(add, "addition")} and {TextUtil.Plural(rem, "removal")}";
        var snippet = Snippet(updated, newString);
        return ToolResult.Ok(
            $"The file {path} has been updated{(count > 1 ? $" ({count} occurrences replaced)" : "")}. Here's the result of running `cat -n` on a snippet of the edited file:\n{snippet}",
            summary, diff);
    }

    private static string Snippet(string content, string newString)
    {
        var index = newString.Length > 0 ? content.IndexOf(newString.Replace("\r\n", "\n").Split('\n')[0], StringComparison.Ordinal) : -1;
        var line = index < 0 ? 1 : content.AsSpan(0, index).Count('\n') + 1;
        var start = Math.Max(1, line - 4);
        return ReadTool.Number(content, start, newString.Split('\n').Length + 8);
    }
}
