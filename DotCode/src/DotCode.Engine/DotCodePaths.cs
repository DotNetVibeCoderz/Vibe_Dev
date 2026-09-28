using System.Text;

namespace DotCode.Engine;

/// <summary>Well-known file locations. DotCode reads its own <c>.dotcode/</c> layout and, for migration,
/// Claude Code's <c>.claude/</c> layout (skills, agents, commands, project settings, CLAUDE.md).</summary>
public static class DotCodePaths
{
    public const string AppName = "DotCode";
    public const string DirName = ".dotcode";
    public const string CompatDirName = ".claude";
    public const string MemoryFileName = "DOTCODE.md";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>User config dir (<c>~/.dotcode</c>), overridable with DOTCODE_CONFIG_DIR.</summary>
    public static string UserDir => Environment.GetEnvironmentVariable("DOTCODE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Home, DirName);
    public static string UserCompatDir => Path.Combine(Home, CompatDirName);
    public static string UserSettings => Path.Combine(UserDir, "settings.json");
    public static string UserMcpConfig => Path.Combine(UserDir, "mcp.json");
    public static string PluginsDir => Path.Combine(UserDir, "plugins");
    public static string ThemesDir => Path.Combine(UserDir, "themes");
    public static string HistoryFile => Path.Combine(UserDir, "history.jsonl");
    public static string ProjectsDir => Path.Combine(UserDir, "projects");

    public static string ManagedDir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName)
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/DotCode" : "/etc/dotcode";
    public static string ManagedSettings => Path.Combine(ManagedDir, "managed-settings.json");

    public static string ProjectDir(string root) => Path.Combine(root, DirName);
    public static string ProjectSettings(string root) => Path.Combine(root, DirName, "settings.json");
    public static string ProjectLocalSettings(string root) => Path.Combine(root, DirName, "settings.local.json");

    /// <summary>Per-project data dir under ~/.dotcode/projects, keyed by a filesystem-safe slug of the path.</summary>
    public static string ProjectDataDir(string cwd) => Path.Combine(ProjectsDir, Slug(cwd));

    public static string Slug(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sb = new StringBuilder(full.Length);
        foreach (var ch in full) sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        return sb.ToString();
    }

    /// <summary>Finds the project root: nearest ancestor with .git, .dotcode or .claude; falls back to cwd.</summary>
    public static string FindProjectRoot(string cwd)
    {
        for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
        {
            // The home directory's .dotcode is the user config dir, not a project marker.
            if (string.Equals(dir.FullName.TrimEnd(Path.DirectorySeparatorChar), Home.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")) ||
                Directory.Exists(Path.Combine(dir.FullName, DirName)))
                return dir.FullName;
            if (dir.FullName == Home) break;
        }
        return cwd;
    }

    public static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(Home, path[2..]) : path == "~" ? Home : path;

    /// <summary>Resolves a possibly-relative path against cwd and normalizes separators.</summary>
    public static string Resolve(string path, string cwd)
    {
        path = ExpandHome(path.Trim().Trim('"'));
        // Git-bash style "/c/Users/..." on Windows
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == '/')
            path = $"{char.ToUpperInvariant(path[1])}:{path[2..]}";
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(cwd, path));
    }

    public static bool IsUnder(string path, string dir)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return p.Equals(d, cmp) || p.StartsWith(d + Path.DirectorySeparatorChar, cmp);
    }

    /// <summary>Path relative to cwd when inside it (for compact UI display), otherwise the full path.</summary>
    public static string Display(string path, string cwd)
    {
        try
        {
            if (IsUnder(path, cwd)) return Path.GetRelativePath(cwd, path);
            if (IsUnder(path, Home)) return "~" + Path.DirectorySeparatorChar + Path.GetRelativePath(Home, path);
        }
        catch (ArgumentException) { }
        return path;
    }
}
