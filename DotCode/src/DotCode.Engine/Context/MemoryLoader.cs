using System.Text;
using System.Text.RegularExpressions;

namespace DotCode.Engine.Context;

public enum MemoryScope { Managed, User, Project, Local }

public sealed record MemoryFile(string Path, MemoryScope Scope, string Content);

/// <summary>Loads instruction files: managed, user (~/.dotcode/DOTCODE.md, ~/.claude/CLAUDE.md) and project files
/// from the project root down to cwd (DOTCODE.md, CLAUDE.md, AGENTS.md, .dotcode/DOTCODE.md, *.local.md).
/// Supports <c>@path</c> imports (depth-limited).</summary>
public static partial class MemoryLoader
{
    private static readonly string[] ProjectNames =
    [
        "DOTCODE.md", Path.Combine(".dotcode", "DOTCODE.md"), "CLAUDE.md", Path.Combine(".claude", "CLAUDE.md"), "AGENTS.md",
    ];
    private static readonly string[] LocalNames = ["DOTCODE.local.md", "CLAUDE.local.md"];

    [GeneratedRegex(@"(?<=^|\s)@((?:~|\.{1,2}|/|[A-Za-z]:)?[\w\-./\\]+\.\w+)", RegexOptions.Multiline)]
    private static partial Regex ImportPattern();

    public static List<MemoryFile> Load(string cwd, string projectRoot)
    {
        var result = new List<MemoryFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, MemoryScope scope)
        {
            if (!File.Exists(path)) return;
            var full = Path.GetFullPath(path);
            if (!seen.Add(full)) return;
            try
            {
                var content = ExpandImports(File.ReadAllText(full), Path.GetDirectoryName(full)!, seen, 0);
                if (content.Trim().Length > 0) result.Add(new MemoryFile(full, scope, content));
            }
            catch (IOException) { }
        }

        Add(Path.Combine(DotCodePaths.ManagedDir, DotCodePaths.MemoryFileName), MemoryScope.Managed);
        Add(Path.Combine(DotCodePaths.UserDir, DotCodePaths.MemoryFileName), MemoryScope.User);
        Add(Path.Combine(DotCodePaths.UserCompatDir, "CLAUDE.md"), MemoryScope.User);

        // Directories from project root down to cwd, so closer files come later (and take precedence).
        var dirs = new List<string>();
        for (var d = new DirectoryInfo(cwd); d is not null; d = d.Parent)
        {
            dirs.Add(d.FullName);
            if (string.Equals(d.FullName.TrimEnd(Path.DirectorySeparatorChar), projectRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
        dirs.Reverse();
        foreach (var dir in dirs)
        {
            foreach (var name in ProjectNames) Add(Path.Combine(dir, name), MemoryScope.Project);
            foreach (var name in LocalNames) Add(Path.Combine(dir, name), MemoryScope.Local);
        }
        return result;
    }

    private static string ExpandImports(string content, string baseDir, HashSet<string> seen, int depth)
    {
        if (depth >= 5 || !content.Contains('@')) return content;
        var sb = new StringBuilder();
        var inFence = false;
        foreach (var line in content.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            sb.Append(line).Append('\n');
            if (inFence || line.Contains('`')) continue;
            foreach (Match m in ImportPattern().Matches(line))
            {
                var rel = m.Groups[1].Value;
                var path = DotCodePaths.Resolve(rel, baseDir);
                if (!File.Exists(path) || !seen.Add(path)) continue;
                try
                {
                    var imported = ExpandImports(File.ReadAllText(path), Path.GetDirectoryName(path)!, seen, depth + 1);
                    sb.Append("\n<!-- imported from ").Append(rel).Append(" -->\n").Append(imported).Append('\n');
                }
                catch (IOException) { }
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Appends a line to the project (or user) memory file, creating it if needed (used by "#" shortcut and /memory).</summary>
    public static string AppendMemory(string projectRoot, string text, bool user)
    {
        var path = user ? Path.Combine(DotCodePaths.UserDir, DotCodePaths.MemoryFileName) : Path.Combine(projectRoot, DotCodePaths.MemoryFileName);
        if (!user && !File.Exists(path) && File.Exists(Path.Combine(projectRoot, "CLAUDE.md"))) path = Path.Combine(projectRoot, "CLAUDE.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var prefix = File.Exists(path) && !File.ReadAllText(path).EndsWith('\n') ? "\n" : "";
        File.AppendAllText(path, $"{prefix}- {text.Trim()}\n");
        return path;
    }
}
