using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;
using DotCode.Engine.Tools;

namespace DotCode.Engine.Permissions;

public sealed record PermissionCheck(PermissionBehavior Behavior, string? Reason = null, string? SuggestedRule = null, PermissionRule? MatchedRule = null)
{
    public static readonly PermissionCheck Allowed = new(PermissionBehavior.Allow);
}

/// <summary>Evaluates tool calls against rules and the active mode. Order: deny rules → plan-mode restriction →
/// ask rules → bypass → allow rules (settings, CLI, session) → built-in defaults per permission kind.</summary>
public sealed class PermissionEngine
{
    private readonly List<PermissionRule> _allow = [];
    private readonly List<PermissionRule> _deny = [];
    private readonly List<PermissionRule> _ask = [];
    private readonly Lock _gate = new();

    public string ProjectRoot { get; }
    public List<string> WorkingDirectories { get; } = [];
    public bool BypassDisabled { get; }

    public PermissionEngine(Settings settings, string cwd, string projectRoot, IEnumerable<string>? cliAllowed = null, IEnumerable<string>? cliDenied = null, IEnumerable<string>? addDirs = null)
    {
        ProjectRoot = projectRoot;
        WorkingDirectories.Add(Path.GetFullPath(cwd));
        var perms = settings.Permissions;
        foreach (var dir in (perms?.AdditionalDirectories ?? []).Concat(addDirs ?? []))
            WorkingDirectories.Add(DotCodePaths.Resolve(dir, cwd));
        Add(_allow, perms?.Allow, "settings");
        Add(_deny, perms?.Deny, "settings");
        Add(_ask, perms?.Ask, "settings");
        Add(_allow, cliAllowed, "cli");
        Add(_deny, cliDenied, "cli");
        BypassDisabled = perms?.DisableBypassPermissionsMode == true;
    }

    private static void Add(List<PermissionRule> list, IEnumerable<string>? rules, string source)
    {
        if (rules is null) return;
        foreach (var raw in rules)
            foreach (var piece in SplitRuleList(raw))
                if (PermissionRule.Parse(piece, source) is { } r) list.Add(r);
    }

    /// <summary>CLI lists accept "Bash(git *) Edit" or comma separated values; parentheses protect inner spaces.</summary>
    public static IEnumerable<string> SplitRuleList(string raw)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if ((c == ',' || c == ' ') && depth == 0)
            {
                if (i > start) yield return raw[start..i];
                start = i + 1;
            }
        }
        if (raw.Length > start) yield return raw[start..];
    }

    public IReadOnlyList<PermissionRule> AllowRules { get { lock (_gate) return [.. _allow]; } }
    public IReadOnlyList<PermissionRule> DenyRules { get { lock (_gate) return [.. _deny]; } }
    public IReadOnlyList<PermissionRule> AskRules { get { lock (_gate) return [.. _ask]; } }

    public void AddSessionRule(string rule, PermissionBehavior behavior = PermissionBehavior.Allow)
    {
        if (PermissionRule.Parse(rule, "session") is not { } r) return;
        lock (_gate)
        {
            var list = behavior switch { PermissionBehavior.Deny => _deny, PermissionBehavior.Ask => _ask, _ => _allow };
            if (!list.Any(x => x.ToString() == r.ToString())) list.Add(r);
        }
    }

    public void RemoveRule(string rule)
    {
        lock (_gate)
        {
            _allow.RemoveAll(r => r.ToString() == rule);
            _deny.RemoveAll(r => r.ToString() == rule);
            _ask.RemoveAll(r => r.ToString() == rule);
        }
    }

    public bool IsInWorkingDirs(string path)
    {
        lock (_gate) return WorkingDirectories.Any(d => DotCodePaths.IsUnder(path, d));
    }

    /// <summary>Adds a working directory at runtime (/add-dir, subagent worktrees).</summary>
    public void AddWorkingDirectory(string dir)
    {
        var full = Path.GetFullPath(dir);
        lock (_gate)
            if (!WorkingDirectories.Any(d => DotCodePaths.IsUnder(full, d))) WorkingDirectories.Add(full);
    }

    public const string SandboxedReason = "sandboxed";

    private static readonly string[] SecretPatterns = [".env", ".env.*", "*.pem", "*.key", "id_rsa", "id_ed25519", "*.pfx", "credentials.json", "secrets.json"];

    public static bool LooksSecret(string path)
    {
        var name = Path.GetFileName(path);
        return SecretPatterns.Any(p => Wildcard.IsMatch(name, p)) && !name.EndsWith(".example", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase);
    }

    public PermissionCheck Evaluate(Tool tool, JsonElement input, PermissionMode mode, Agent.AgentSession session)
    {
        var target = tool.GetPermissionTarget(input, session);
        List<PermissionRule> deny, ask, allow;
        lock (_gate) { deny = [.. _deny]; ask = [.. _ask]; allow = [.. _allow]; }

        foreach (var rule in deny)
            if (Matches(rule, tool, target, session, forDeny: true))
                return new PermissionCheck(PermissionBehavior.Deny, $"Permission to use {tool.Name} has been denied by rule {rule}", MatchedRule: rule);

        if (mode == PermissionMode.Plan && !tool.IsReadOnly(input) && target.Kind is not PermissionKind.None)
        {
            var allowedInPlan = tool.Name is "ExitPlanMode" or "TodoWrite" or "AskUserQuestion" or "Agent" or "Skill" or "WebFetch" or "WebSearch";
            if (!allowedInPlan)
                return new PermissionCheck(PermissionBehavior.Deny, "Plan mode is active: do not make changes yet. Research with read-only tools, then present your plan with ExitPlanMode.");
        }

        if (mode != PermissionMode.BypassPermissions)
            foreach (var rule in ask)
                if (Matches(rule, tool, target, session, forDeny: false))
                    return new PermissionCheck(PermissionBehavior.Ask, $"Rule {rule} requires confirmation", Suggest(tool, target, input), rule);

        if (mode == PermissionMode.BypassPermissions) return PermissionCheck.Allowed;

        foreach (var rule in allow)
            if (Matches(rule, tool, target, session, forDeny: false))
                return new PermissionCheck(PermissionBehavior.Allow, MatchedRule: rule);

        // Commands confined by an OS sandbox (writes limited to the working dirs) need no prompt; deny and ask rules
        // above still apply, and dangerously_disable_sandbox falls through to the normal defaults.
        if (target.Kind == PermissionKind.Shell && Sandbox.ShellSandbox.AutoAllows(session, input))
            return new PermissionCheck(PermissionBehavior.Allow, SandboxedReason);

        return Defaults(tool, input, target, mode);
    }

    private PermissionCheck Defaults(Tool tool, JsonElement input, PermissionTarget target, PermissionMode mode)
    {
        var suggestion = Suggest(tool, target, input);
        switch (target.Kind)
        {
            case PermissionKind.None:
            case PermissionKind.Agent:
            case PermissionKind.Skill:
                return PermissionCheck.Allowed;
            case PermissionKind.ReadFile:
                if (target.Value is null) return PermissionCheck.Allowed;
                if (IsInWorkingDirs(target.Value) && !LooksSecret(target.Value)) return PermissionCheck.Allowed;
                return new PermissionCheck(PermissionBehavior.Ask, LooksSecret(target.Value) ? "File may contain secrets" : "Path is outside the working directories", suggestion);
            case PermissionKind.EditFile:
                if (mode is PermissionMode.AcceptEdits or PermissionMode.Auto && target.Value is not null && IsInWorkingDirs(target.Value)) return PermissionCheck.Allowed;
                return new PermissionCheck(PermissionBehavior.Ask, null, suggestion);
            case PermissionKind.Shell:
                if (target.Value is not null && ShellCommand.IsReadOnly(target.Value)) return PermissionCheck.Allowed;
                if (mode == PermissionMode.AcceptEdits && target.Value is not null && IsFileSystemCommand(target.Value)) return PermissionCheck.Allowed;
                return new PermissionCheck(PermissionBehavior.Ask, null, suggestion);
            default:
                return new PermissionCheck(PermissionBehavior.Ask, null, suggestion);
        }
    }

    private static bool IsFileSystemCommand(string command)
    {
        var a = ShellCommand.Analyze(command);
        return !a.HasSubstitution && a.Subcommands.All(s => s.Split(' ', 2)[0] is "mkdir" or "touch" or "cd" or "New-Item");
    }

    private bool Matches(PermissionRule rule, Tool tool, PermissionTarget target, Agent.AgentSession session, bool forDeny)
    {
        var toolMatches = rule.MatchesTool(tool.Name) ||
                          target.Kind == PermissionKind.ReadFile && rule.Tool == "Read" ||
                          target.Kind == PermissionKind.EditFile && rule.Tool is "Edit" or "Write";
        if (!toolMatches) return false;
        if (rule.Specifier is null) return true;
        var value = target.Value;
        if (value is null) return false;

        switch (target.Kind)
        {
            case PermissionKind.Shell:
                var analysis = ShellCommand.Analyze(value);
                if (analysis.Subcommands.Count == 0) return false;
                // Deny if any subcommand matches; allow only if all do (and nothing sneaky is going on).
                if (forDeny) return analysis.Subcommands.Any(s => ShellSpecMatches(rule.Specifier, s)) || ShellSpecMatches(rule.Specifier, value);
                if (ShellSpecMatches(rule.Specifier, value) && analysis.Subcommands.Count == 1) return true;
                return !analysis.HasSubstitution && analysis.Subcommands.All(s => ShellSpecMatches(rule.Specifier, s) || s.StartsWith("cd ", StringComparison.Ordinal));
            case PermissionKind.ReadFile or PermissionKind.EditFile:
                return PathPattern.IsMatch(value, rule.Specifier, ProjectRoot, session.Cwd);
            case PermissionKind.Web:
                if (rule.Specifier.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
                {
                    var domain = rule.Specifier[7..].Trim().ToLowerInvariant();
                    var host = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : value.ToLowerInvariant();
                    return domain.StartsWith("*.", StringComparison.Ordinal) ? host.EndsWith(domain[1..], StringComparison.Ordinal) : host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
                }
                return Wildcard.IsMatch(value, rule.Specifier);
            default:
                return Wildcard.IsMatch(value, rule.Specifier) || string.Equals(value, rule.Specifier, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool ShellSpecMatches(string spec, string command)
    {
        command = command.Trim();
        if (spec.EndsWith(":*", StringComparison.Ordinal))
        {
            var prefix = spec[..^2].TrimEnd();
            return command == prefix || command.StartsWith(prefix + " ", StringComparison.Ordinal) || command.StartsWith(prefix, StringComparison.Ordinal) && prefix.EndsWith(' ');
        }
        if (spec.Contains('*'))
        {
            // "git *" should also match bare "git"
            if (spec.EndsWith(" *", StringComparison.Ordinal) && command == spec[..^2]) return true;
            return Wildcard.IsMatch(command, spec);
        }
        return command == spec;
    }

    /// <summary>Rule offered by "Yes, and don't ask again".</summary>
    public string? Suggest(Tool tool, PermissionTarget target, JsonElement input)
    {
        switch (target.Kind)
        {
            case PermissionKind.Shell when target.Value is { } cmd:
                var subs = ShellCommand.Analyze(cmd).Subcommands.Where(s => !s.StartsWith("cd ", StringComparison.Ordinal)).ToList();
                if (subs.Count != 1) return null;
                return $"{tool.Name}({ShellCommand.SuggestPrefix(subs[0])}:*)";
            case PermissionKind.Web when target.Value is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri):
                return $"{tool.Name}(domain:{uri.Host})";
            case PermissionKind.ReadFile when target.Value is { } path:
                var dir = Path.GetDirectoryName(path);
                return dir is null ? null : $"Read(//{dir.Replace('\\', '/').TrimStart('/')}/**)";
            case PermissionKind.EditFile:
                return null; // edits use "allow all edits this session" (switches to acceptEdits)
            case PermissionKind.Mcp:
                return tool.Name;
            default:
                return tool.Name;
        }
    }
}
