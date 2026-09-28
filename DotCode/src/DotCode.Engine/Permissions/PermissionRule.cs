using System.Text;
using System.Text.RegularExpressions;

namespace DotCode.Engine.Permissions;

public enum PermissionMode { Default, AcceptEdits, Plan, BypassPermissions, Auto }

public static class PermissionModes
{
    public static PermissionMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "acceptedits" or "accept-edits" or "accept_edits" => PermissionMode.AcceptEdits,
        "plan" => PermissionMode.Plan,
        "auto" or "automode" or "auto-mode" => PermissionMode.Auto,
        "bypasspermissions" or "bypass" or "bypass-permissions" or "yolo" => PermissionMode.BypassPermissions,
        _ => PermissionMode.Default,
    };

    public static string ToSetting(this PermissionMode mode) => mode switch
    {
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.Plan => "plan",
        PermissionMode.BypassPermissions => "bypassPermissions",
        PermissionMode.Auto => "auto",
        _ => "default",
    };

    /// <summary>Shift+Tab cycle order: default → accept edits → (auto) → plan → (bypass). Auto and bypass are only
    /// part of the cycle when enabled.</summary>
    public static PermissionMode Next(this PermissionMode mode, bool includeBypass, bool includeAuto = false) => mode switch
    {
        PermissionMode.Default => PermissionMode.AcceptEdits,
        PermissionMode.AcceptEdits => includeAuto ? PermissionMode.Auto : PermissionMode.Plan,
        PermissionMode.Auto => PermissionMode.Plan,
        PermissionMode.Plan => includeBypass ? PermissionMode.BypassPermissions : PermissionMode.Default,
        _ => PermissionMode.Default,
    };
}

/// <summary>A permission rule in Claude Code syntax: <c>Tool</c> or <c>Tool(specifier)</c>.
/// Examples: <c>Bash(npm run test:*)</c>, <c>Bash(git *)</c>, <c>Read(~/secrets/**)</c>, <c>Edit(/src/**)</c>,
/// <c>WebFetch(domain:example.com)</c>, <c>mcp__github</c>, <c>Agent(Explore)</c>, <c>Skill(deploy *)</c>.</summary>
public sealed partial record PermissionRule(string Tool, string? Specifier, string Source)
{
    [GeneratedRegex(@"^\s*([A-Za-z0-9_\-\*\.]+)\s*(?:\((.*)\))?\s*$", RegexOptions.Singleline)]
    private static partial Regex RulePattern();

    public static PermissionRule? Parse(string rule, string source = "")
    {
        var m = RulePattern().Match(rule);
        if (!m.Success) return null;
        var spec = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
        if (spec is "" or "*") spec = null;
        return new PermissionRule(m.Groups[1].Value, spec, source);
    }

    public override string ToString() => Specifier is null ? Tool : $"{Tool}({Specifier})";

    /// <summary>Tool-name part match: exact, MCP server prefix (<c>mcp__server</c>), or wildcard.</summary>
    public bool MatchesTool(string toolName)
    {
        if (Tool == "*" || string.Equals(Tool, toolName, StringComparison.Ordinal)) return true;
        if (Tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var t = Tool.EndsWith("__*", StringComparison.Ordinal) ? Tool[..^3] : Tool;
            if (toolName.StartsWith(t + "__", StringComparison.Ordinal)) return true;
        }
        if (Tool.Contains('*')) return Wildcard.IsMatch(toolName, Tool);
        return false;
    }
}

public static class Wildcard
{
    /// <summary>Simple wildcard match where <c>*</c> matches any run of characters (including spaces and slashes).</summary>
    public static bool IsMatch(string text, string pattern)
    {
        int t = 0, p = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == text[t])
            {
                t++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <summary>Gitignore-style path globs: <c>**</c> spans directories, <c>*</c> stays within a segment.
/// Anchors: <c>//abs</c> absolute, <c>~/</c> home, <c>/x</c> relative to the settings root, plain → anywhere under cwd.</summary>
public static class PathPattern
{
    private static readonly Dictionary<string, Regex> Cache = new();
    private static readonly Lock Gate = new();

    public static bool IsMatch(string absolutePath, string pattern, string root, string cwd)
    {
        var full = Normalize(absolutePath);
        string anchored;
        if (pattern.StartsWith("//", StringComparison.Ordinal)) anchored = pattern[1..];
        else if (pattern.StartsWith("~/", StringComparison.Ordinal)) anchored = Normalize(DotCodePaths.Home) + pattern[1..];
        else if (pattern.StartsWith('/') && !(OperatingSystem.IsWindows() && pattern.Length > 2 && pattern[2] == ':')) anchored = Normalize(root) + pattern;
        else if (Path.IsPathRooted(pattern)) anchored = Normalize(pattern);
        else anchored = Normalize(cwd) + "/**/" + (pattern.StartsWith("./", StringComparison.Ordinal) ? pattern[2..] : pattern);

        // Windows drive-letter normalisation: "/C:/x" -> "C:/x"
        if (OperatingSystem.IsWindows() && anchored.Length > 3 && anchored[0] == '/' && anchored[2] == ':') anchored = anchored[1..];
        var regex = GetRegex(Normalize(anchored));
        return regex.IsMatch(full) || (anchored.EndsWith("/**", StringComparison.Ordinal) && regex.IsMatch(full + "/"));
    }

    private static string Normalize(string p) => p.Replace('\\', '/');

    private static Regex GetRegex(string glob)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(glob, out var r)) return r;
            var sb = new StringBuilder("^");
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                if (c == '*')
                {
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // "**/" matches zero or more directories; trailing "**" matches everything
                        if (i + 2 < glob.Length && glob[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 2; }
                        else { sb.Append(".*"); i++; }
                    }
                    else sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            var options = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? RegexOptions.IgnoreCase : RegexOptions.None;
            r = new Regex(sb.ToString(), options | RegexOptions.CultureInvariant);
            Cache[glob] = r;
            return r;
        }
    }
}
