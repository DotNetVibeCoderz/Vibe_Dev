// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text;
using System.Text.Json;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Utilities;

namespace AutoCode.Tools;

/// <summary>Reads a slice of a file with line numbers, the way <c>cat -n</c> would.</summary>
public sealed class ReadTool : ToolBase
{
    private const int DefaultLimit = 2000;
    private const int MaxLineLength = 2000;

    public override string Name => "Read";

    public override string Description =>
        """
        Read a file from the local filesystem.

        - file_path must be an absolute path, or a path relative to the workspace root.
        - Output is line-numbered starting at 1, in `cat -n` style.
        - Reads up to 2000 lines by default. Use offset and limit to page through larger files.
        - Long lines are truncated at 2000 characters.
        - Reading a file is what makes it eligible for Edit: Auto Code refuses to patch a file it has
          not seen, so read before you edit.
        """;

    public override ToolCapability Capability => ToolCapability.ReadsFiles;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Absolute or workspace-relative path to read." },
            "offset": { "type": "integer", "description": "1-based line number to start from." },
            "limit": { "type": "integer", "description": "Maximum number of lines to return." }
          },
          "required": ["file_path"]
        }
        """;

    public override string Summarize(JsonElement arguments) =>
        $"Read({Peek(arguments, "file_path") ?? "?"})";

    protected override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = WorkspacePath.Resolve(invocation.WorkspaceRoot, invocation.GetString("file_path"));

        if (Directory.Exists(path))
            return ToolResult.Fail($"'{path}' is a directory. Use the List tool to enumerate it.");

        if (!File.Exists(path))
            return ToolResult.Fail($"File not found: {path}");

        var info = new FileInfo(path);
        if (IsBinaryExtension(info.Extension))
            return ToolResult.Fail($"'{info.Name}' looks like a binary file ({info.Length:N0} bytes) and cannot be read as text.");

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        if (content.Length == 0)
            return ToolResult.Ok("(file is empty)");

        // Track the full content, not the returned slice: an edit must compare against the real file.
        invocation.Services.Files.RecordRead(path, content);

        var lines = content.Split('\n');
        var offset = Math.Max(1, invocation.TryGetInt("offset") ?? 1);
        var limit = Math.Clamp(invocation.TryGetInt("limit") ?? DefaultLimit, 1, 50_000);

        if (offset > lines.Length)
            return ToolResult.Fail($"offset {offset} is past the end of the file ({lines.Length} lines).");

        var builder = new StringBuilder();
        var end = Math.Min(lines.Length, offset - 1 + limit);

        for (var i = offset - 1; i < end; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > MaxLineLength)
                line = line[..MaxLineLength] + "… [truncated]";

            builder.Append((i + 1).ToString().PadLeft(6)).Append('\t').Append(line).Append('\n');
        }

        if (end < lines.Length)
            builder.Append($"\n… {lines.Length - end:N0} more lines. Re-read with offset={end + 1} to continue.\n");

        var display = $"Read {WorkspacePath.Relative(invocation.WorkspaceRoot, path)} ({end - offset + 1} lines)";
        return ToolResult.Ok(builder.ToString(), display);
    }

    private static bool IsBinaryExtension(string extension) =>
        extension.ToLowerInvariant() is
            ".exe" or ".dll" or ".so" or ".dylib" or ".pdb" or ".zip" or ".gz" or ".tar" or ".7z" or ".rar" or
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp" or ".mp3" or ".mp4" or ".mov" or
            ".pdf" or ".class" or ".jar" or ".wasm" or ".bin" or ".dat" or ".db" or ".sqlite";
}

/// <summary>Creates or overwrites a file.</summary>
public sealed class WriteTool : ToolBase
{
    public override string Name => "Write";

    public override string Description =>
        """
        Write a file to the local filesystem, creating parent directories as needed.

        - Overwrites the file if it already exists, so read it first unless you intend a full replacement.
        - Prefer Edit for partial changes: it is cheaper and far less likely to lose surrounding work.
        """;

    public override ToolCapability Capability => ToolCapability.WritesFiles;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Absolute or workspace-relative path to write." },
            "content": { "type": "string", "description": "Full contents of the file." }
          },
          "required": ["file_path", "content"]
        }
        """;

    public override string Summarize(JsonElement arguments) =>
        $"Write({Peek(arguments, "file_path") ?? "?"})";

    protected override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = WorkspacePath.Resolve(invocation.WorkspaceRoot, invocation.GetString("file_path"));
        var content = invocation.GetString("content");
        var existed = File.Exists(path);

        if (existed && !invocation.Services.Files.HasRead(path))
        {
            var current = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (current.Length > 0)
            {
                return ToolResult.Fail(
                    $"'{WorkspacePath.Relative(invocation.WorkspaceRoot, path)}' already exists and has not been read " +
                    "in this session. Read it first so the overwrite is deliberate.");
            }
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        invocation.Services.Files.RecordWrite(path, content);

        var relative = WorkspacePath.Relative(invocation.WorkspaceRoot, path);
        var lineCount = content.Count(c => c == '\n') + 1;

        return ToolResult.Ok(
            $"{(existed ? "Updated" : "Created")} {relative} ({lineCount:N0} lines).",
            $"{(existed ? "Updated" : "Created")} {relative}");
    }
}

/// <summary>Exact-string replacement inside an already-read file.</summary>
public sealed class EditTool : ToolBase
{
    public override string Name => "Edit";

    public override string Description =>
        """
        Perform an exact string replacement in a file.

        - The file must have been read in this session first.
        - old_string must appear exactly once unless replace_all is true; a non-unique match is an
          error, because guessing which occurrence was meant is how edits corrupt files.
        - Include enough surrounding context in old_string to make the match unambiguous.
        - Strip the line-number prefix that Read adds before using text as old_string.
        """;

    public override ToolCapability Capability => ToolCapability.WritesFiles;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Absolute or workspace-relative path to modify." },
            "old_string": { "type": "string", "description": "Exact text to replace." },
            "new_string": { "type": "string", "description": "Replacement text. Must differ from old_string." },
            "replace_all": { "type": "boolean", "description": "Replace every occurrence instead of requiring uniqueness." }
          },
          "required": ["file_path", "old_string", "new_string"]
        }
        """;

    public override string Summarize(JsonElement arguments) =>
        $"Edit({Peek(arguments, "file_path") ?? "?"})";

    protected override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = WorkspacePath.Resolve(invocation.WorkspaceRoot, invocation.GetString("file_path"));
        var oldString = invocation.GetString("old_string");
        var newString = invocation.GetString("new_string");
        var replaceAll = invocation.TryGetBool("replace_all") ?? false;

        if (oldString == newString)
            return ToolResult.Fail("old_string and new_string are identical; nothing to do.");

        if (!File.Exists(path))
            return ToolResult.Fail($"File not found: {path}");

        var relative = WorkspacePath.Relative(invocation.WorkspaceRoot, path);

        if (!invocation.Services.Files.HasRead(path))
            return ToolResult.Fail($"Read {relative} before editing it.");

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        if (invocation.Services.Files.HasChangedSinceRead(path, content))
            return ToolResult.Fail($"{relative} changed on disk since it was read. Read it again before editing.");

        var (updated, count, error) = ApplyEdit(content, oldString, newString, replaceAll);
        if (error is not null)
            return ToolResult.Fail($"{error} in {relative}");

        await File.WriteAllTextAsync(path, updated, cancellationToken).ConfigureAwait(false);
        invocation.Services.Files.RecordWrite(path, updated);

        return ToolResult.Ok(
            $"Applied {count} replacement(s) in {relative}.",
            $"Edited {relative} ({count} replacement{(count == 1 ? "" : "s")})");
    }

    /// <summary>Shared by <see cref="EditTool"/> and <see cref="MultiEditTool"/>.</summary>
    internal static (string Updated, int Count, string? Error) ApplyEdit(
        string content,
        string oldString,
        string newString,
        bool replaceAll)
    {
        var occurrences = CountOccurrences(content, oldString);

        if (occurrences == 0)
            return (content, 0, $"old_string was not found");

        if (occurrences > 1 && !replaceAll)
            return (content, 0, $"old_string appears {occurrences} times; pass replace_all or add more context");

        var updated = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(content, oldString, newString);

        return (updated, replaceAll ? occurrences : 1, null);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
            return 0;

        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string haystack, string needle, string replacement)
    {
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? haystack : haystack[..index] + replacement + haystack[(index + needle.Length)..];
    }
}

/// <summary>Applies several edits to one file atomically.</summary>
public sealed class MultiEditTool : ToolBase
{
    public override string Name => "MultiEdit";

    public override string Description =>
        """
        Apply a sequence of exact string replacements to a single file in one transaction.

        - Edits apply in order; each one sees the result of the previous.
        - If any edit fails the file is left untouched, so a partial rewrite can never happen.
        - Same rules as Edit: read the file first, and each old_string must be unique unless
          replace_all is set on that entry.
        """;

    public override ToolCapability Capability => ToolCapability.WritesFiles;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Absolute or workspace-relative path to modify." },
            "edits": {
              "type": "array",
              "description": "Replacements to apply in order.",
              "items": {
                "type": "object",
                "properties": {
                  "old_string": { "type": "string" },
                  "new_string": { "type": "string" },
                  "replace_all": { "type": "boolean" }
                },
                "required": ["old_string", "new_string"]
              }
            }
          },
          "required": ["file_path", "edits"]
        }
        """;

    public override string Summarize(JsonElement arguments) =>
        $"MultiEdit({Peek(arguments, "file_path") ?? "?"})";

    protected override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = WorkspacePath.Resolve(invocation.WorkspaceRoot, invocation.GetString("file_path"));
        var edits = invocation.TryGetArray("edits");

        if (edits.Count == 0)
            return ToolResult.Fail("edits must contain at least one replacement.");

        if (!File.Exists(path))
            return ToolResult.Fail($"File not found: {path}");

        var relative = WorkspacePath.Relative(invocation.WorkspaceRoot, path);

        if (!invocation.Services.Files.HasRead(path))
            return ToolResult.Fail($"Read {relative} before editing it.");

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        if (invocation.Services.Files.HasChangedSinceRead(path, content))
            return ToolResult.Fail($"{relative} changed on disk since it was read. Read it again before editing.");

        var working = content;
        var applied = 0;

        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];

            if (!edit.TryGetProperty("old_string", out var oldNode) || oldNode.ValueKind != JsonValueKind.String ||
                !edit.TryGetProperty("new_string", out var newNode) || newNode.ValueKind != JsonValueKind.String)
            {
                return ToolResult.Fail($"edits[{i}] must supply string old_string and new_string.");
            }

            var replaceAll = edit.TryGetProperty("replace_all", out var all) && all.ValueKind == JsonValueKind.True;

            var (updated, count, error) = EditTool.ApplyEdit(
                working, oldNode.GetString()!, newNode.GetString()!, replaceAll);

            if (error is not null)
                return ToolResult.Fail($"edits[{i}]: {error} in {relative}. No changes were written.");

            working = updated;
            applied += count;
        }

        await File.WriteAllTextAsync(path, working, cancellationToken).ConfigureAwait(false);
        invocation.Services.Files.RecordWrite(path, working);

        return ToolResult.Ok(
            $"Applied {edits.Count} edit(s), {applied} replacement(s), in {relative}.",
            $"Edited {relative} ({edits.Count} edits)");
    }
}
