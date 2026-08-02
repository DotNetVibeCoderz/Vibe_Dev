// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Abstractions;
using AutoCode.Core.Configuration;
using AutoCode.Core.Utilities;

namespace AutoCode.Core.Permissions;

/// <summary>
/// Decides whether a tool call may proceed.
///
/// EN: order of evaluation is deny → mode → allow → ask. Deny always wins, so a deny rule cannot be
/// undone by a broader allow rule or by an unattended permission mode.
/// ID: urutan evaluasi adalah deny → mode → allow → ask. Aturan deny selalu menang, sehingga tidak
/// bisa dibatalkan oleh aturan allow yang lebih luas maupun oleh mode izin otomatis.
/// </summary>
public sealed class PermissionEngine(AutoCodeOptions options)
{
    private readonly PermissionRules _rules = options.Permissions.Clone();
    private readonly Lock _gate = new();

    /// <summary>Live permission posture; <c>/permissions</c> and Shift+Tab change it mid-session.</summary>
    public PermissionMode Mode { get; set; } = options.PermissionMode;

    /// <summary>Rules added during the session by "always allow"/"always deny" answers.</summary>
    public PermissionRules Rules => _rules;

    /// <summary>
    /// Classifies a pending call without prompting. The caller prompts only when the answer is
    /// <see cref="PermissionCheck.Ask"/>.
    /// </summary>
    public PermissionCheck Evaluate(IAgentTool tool, string subject)
    {
        lock (_gate)
        {
            if (MatchesAny(_rules.Deny, tool.Name, subject, out var denyRule))
                return PermissionCheck.Denied($"blocked by deny rule '{denyRule}'");

            if (options.DisabledTools.Any(pattern => Glob.IsMatch(pattern, tool.Name)))
                return PermissionCheck.Denied($"tool '{tool.Name}' is disabled by configuration");

            // Read-only work never needs approval, in any mode.
            if ((tool.Capability & ToolCapability.Mutating) == 0 &&
                (tool.Capability & ToolCapability.AccessesNetwork) == 0)
            {
                return PermissionCheck.Allowed;
            }

            switch (Mode)
            {
                case PermissionMode.BypassPermissions:
                    return PermissionCheck.Allowed;

                case PermissionMode.Plan when (tool.Capability & ToolCapability.Mutating) != 0:
                    return PermissionCheck.Denied(
                        "Plan mode is active: no files may be written and no commands may be run. " +
                        "Finish researching and present a plan instead.");

                case PermissionMode.AcceptEdits
                    when (tool.Capability & ToolCapability.WritesFiles) != 0 &&
                         (tool.Capability & ToolCapability.ExecutesCommands) == 0:
                    return PermissionCheck.Allowed;
            }

            if (MatchesAny(_rules.Ask, tool.Name, subject, out _))
                return PermissionCheck.Ask;

            if (MatchesAny(_rules.Allow, tool.Name, subject, out _))
                return PermissionCheck.Allowed;

            return PermissionCheck.Ask;
        }
    }

    /// <summary>Persists a rule produced by an "always" answer for the rest of the session.</summary>
    public void AddRule(PermissionOutcome outcome, string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
            return;

        lock (_gate)
        {
            var target = outcome switch
            {
                PermissionOutcome.AllowAlways => _rules.Allow,
                PermissionOutcome.DenyAlways => _rules.Deny,
                _ => null,
            };

            if (target is not null && !target.Contains(rule, StringComparer.OrdinalIgnoreCase))
                target.Add(rule);
        }
    }

    /// <summary>
    /// Builds the rule string an "always allow" answer would persist. For commands this generalises
    /// to the first token plus a wildcard (<c>Bash(npm *)</c>); for paths it keeps the concrete path.
    /// </summary>
    public static string SuggestRule(string toolName, string subject, ToolCapability capability)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return toolName;

        if ((capability & ToolCapability.ExecutesCommands) != 0)
        {
            var head = subject.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return head.Length switch
            {
                0 => toolName,
                1 => $"{toolName}({head[0]})",
                _ => $"{toolName}({head[0]} {head[1]}:*)",
            };
        }

        return $"{toolName}({subject})";
    }

    private static bool MatchesAny(
        List<string> rules,
        string toolName,
        string subject,
        out string matched)
    {
        foreach (var rule in rules)
        {
            if (Matches(rule, toolName, subject))
            {
                matched = rule;
                return true;
            }
        }

        matched = "";
        return false;
    }

    /// <summary>
    /// A rule is either <c>Tool</c> (matches every call to that tool) or <c>Tool(pattern)</c>.
    /// The trailing <c>:*</c> convention means "this prefix and anything after it".
    /// </summary>
    internal static bool Matches(string rule, string toolName, string subject)
    {
        if (string.IsNullOrWhiteSpace(rule))
            return false;

        rule = rule.Trim();

        var open = rule.IndexOf('(');
        if (open < 0 || !rule.EndsWith(')'))
            return Glob.IsMatch(rule, toolName);

        var rulTool = rule[..open].Trim();
        if (!Glob.IsMatch(rulTool, toolName))
            return false;

        var pattern = rule[(open + 1)..^1].Trim();
        if (pattern.Length == 0)
            return true;

        if (pattern.EndsWith(":*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            return subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return pattern.Contains('/') || pattern.Contains('\\')
            ? Glob.IsPathMatch(pattern, subject)
            : Glob.IsMatch(pattern, subject);
    }
}

/// <summary>Outcome of a non-interactive permission classification.</summary>
public readonly record struct PermissionCheck(PermissionCheckKind Kind, string? Reason)
{
    public static PermissionCheck Allowed { get; } = new(PermissionCheckKind.Allow, null);
    public static PermissionCheck Ask { get; } = new(PermissionCheckKind.Ask, null);
    public static PermissionCheck Denied(string reason) => new(PermissionCheckKind.Deny, reason);
}

public enum PermissionCheckKind
{
    Allow = 0,
    Ask = 1,
    Deny = 2,
}
