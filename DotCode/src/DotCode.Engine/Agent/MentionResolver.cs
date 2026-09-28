using System.Text;
using System.Text.RegularExpressions;
using DotCode.Abstractions;
using DotCode.Engine.Tools.Builtin;

namespace DotCode.Engine.Agent;

/// <summary>Resolves <c>@path</c> mentions in a prompt into attached file contents (like Claude Code), marking the
/// files as read so the model can edit them immediately. Directories become a shallow listing.</summary>
public static partial class MentionResolver
{
    [GeneratedRegex(@"(?<=^|\s)@(""[^""]+""|[^\s""']+)")]
    private static partial Regex MentionPattern();

    private const int MaxFileBytes = 256 * 1024;

    public static IEnumerable<ContentPart> Resolve(string prompt, AgentSession session)
    {
        if (!prompt.Contains('@')) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in MentionPattern().Matches(prompt))
        {
            var raw = m.Groups[1].Value.Trim('"').TrimEnd(',', '.', ';', ':', ')');
            if (raw.Length == 0 || raw.Contains("://", StringComparison.Ordinal)) continue;
            // Agent mentions ("@agent-Explore") are handled by the model via the Agent tool.
            if (raw.StartsWith("agent-", StringComparison.Ordinal)) continue;
            string path;
            try { path = DotCodePaths.Resolve(raw, session.Cwd); }
            catch (Exception) { continue; }
            if (!seen.Add(path)) continue;

            if (Directory.Exists(path))
            {
                var sb = new StringBuilder();
                foreach (var entry in Directory.EnumerateFileSystemEntries(path).OrderBy(e => e).Take(200))
                    sb.Append(Directory.Exists(entry) ? Path.GetFileName(entry) + "/" : Path.GetFileName(entry)).Append('\n');
                yield return new TextPart($"<system-reminder>\nThe user mentioned directory {raw}. Its contents:\n{sb}</system-reminder>");
                continue;
            }
            if (!File.Exists(path)) continue;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ReadTool.ImageTypes.TryGetValue(ext, out var media))
            {
                yield return new ImagePart(Convert.ToBase64String(File.ReadAllBytes(path)), media);
                continue;
            }
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                yield return new TextPart($"<system-reminder>\nThe user mentioned {raw} ({info.Length:N0} bytes, too large to attach). Use the Read tool with offset/limit.\n</system-reminder>");
                continue;
            }
            string content;
            try { content = File.ReadAllText(path); }
            catch (IOException) { continue; }
            session.FileState.MarkRead(path);
            yield return new TextPart($"<system-reminder>\nContents of {path} (mentioned by the user, already read with the Read tool):\n\n{ReadTool.Number(content, 1, int.MaxValue)}\n</system-reminder>");
        }
    }
}
