using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;
using DotCode.Engine.Util;

namespace DotCode.Engine.Hooks;

public static class HookEvents
{
    public const string PreToolUse = "PreToolUse";
    public const string PostToolUse = "PostToolUse";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string Stop = "Stop";
    public const string SubagentStop = "SubagentStop";
    public const string SessionStart = "SessionStart";
    public const string SessionEnd = "SessionEnd";
    public const string Notification = "Notification";
    public const string PreCompact = "PreCompact";

    public static readonly string[] All = [PreToolUse, PostToolUse, UserPromptSubmit, Stop, SubagentStop, SessionStart, SessionEnd, Notification, PreCompact];
}

/// <summary>Aggregated result of the hooks that ran for one event.</summary>
public sealed class HookOutcome
{
    /// <summary>Block the action (exit code 2 or <c>"decision":"block"</c>); <see cref="Reason"/> is fed back to the model.</summary>
    public bool Block { get; set; }
    public string? Reason { get; set; }
    /// <summary>PreToolUse permission override: allow | deny | ask.</summary>
    public string? PermissionDecision { get; set; }
    public JsonElement? UpdatedInput { get; set; }
    /// <summary>Extra context injected into the conversation (UserPromptSubmit, SessionStart, PostToolUse).</summary>
    public List<string> AdditionalContext { get; } = [];
    /// <summary><c>"continue": false</c> stops the agent entirely.</summary>
    public bool StopAgent { get; set; }
    public string? StopReason { get; set; }
    public List<string> SystemMessages { get; } = [];
    public int HooksRun { get; set; }

    public static readonly HookOutcome None = new();
}

/// <summary>Runs command hooks configured in settings (Claude Code compatible format and JSON I/O contract):
/// the event payload is written to stdin as JSON; exit 0 = success (stdout may contain a JSON decision),
/// exit 2 = block with stderr as the reason, other codes = non-blocking error.</summary>
public sealed class HookRunner(Dictionary<string, List<HookMatcher>> hooks, string cwd, bool disabled = false)
{
    public Dictionary<string, List<HookMatcher>> Hooks { get; } = hooks;

    public bool Has(string eventName) => !disabled && Hooks.TryGetValue(eventName, out var m) && m.Count > 0;

    public static HookRunner Create(Settings settings, Dictionary<string, List<HookMatcher>> pluginHooks, string cwd)
    {
        var merged = new Dictionary<string, List<HookMatcher>>(StringComparer.Ordinal);
        foreach (var source in new[] { settings.Hooks, pluginHooks })
        {
            if (source is null) continue;
            foreach (var (ev, matchers) in source)
            {
                if (!merged.TryGetValue(ev, out var list)) merged[ev] = list = [];
                list.AddRange(matchers);
            }
        }
        return new HookRunner(merged, cwd, settings.DisableAllHooks == true);
    }

    public async Task<HookOutcome> RunAsync(string eventName, string? matchValue, Action<Utf8JsonWriter> writePayload, string sessionId, string transcriptPath, CancellationToken ct)
    {
        if (disabled || !Hooks.TryGetValue(eventName, out var matchers)) return HookOutcome.None;
        var commands = matchers.Where(m => MatcherApplies(m.Matcher, matchValue)).SelectMany(m => m.Hooks).Where(h => h.Type == "command" && h.Command.Length > 0).ToList();
        if (commands.Count == 0) return HookOutcome.None;

        var payload = DotCodeJson.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("session_id", sessionId);
            w.WriteString("transcript_path", transcriptPath);
            w.WriteString("cwd", cwd);
            w.WriteString("hook_event_name", eventName);
            writePayload(w);
            w.WriteEndObject();
        }).GetRawText();

        var outcome = new HookOutcome();
        // Hooks for the same event run in parallel.
        var results = await Task.WhenAll(commands.Select(h => RunOne(h, payload, ct))).ConfigureAwait(false);
        foreach (var (hook, result) in commands.Zip(results))
        {
            outcome.HooksRun++;
            Apply(outcome, eventName, result);
        }
        return outcome;
    }

    private static bool MatcherApplies(string? matcher, string? value)
    {
        if (string.IsNullOrEmpty(matcher) || matcher == "*" || value is null) return true;
        if (matcher.All(c => char.IsLetterOrDigit(c) || c is '_' or '|'))
            return matcher.Split('|').Any(m => string.Equals(m, value, StringComparison.Ordinal));
        try { return Regex.IsMatch(value, "^(?:" + matcher + ")$"); }
        catch (ArgumentException) { return false; }
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunOne(HookCommand hook, string payload, CancellationToken ct)
    {
        ProcessStartInfo psi;
        if (ProcessRunner.BashPath is { } bash)
        {
            psi = new ProcessStartInfo(bash) { WorkingDirectory = cwd };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(hook.Command);
        }
        else if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("cmd.exe") { WorkingDirectory = cwd };
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(hook.Command);
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sh") { WorkingDirectory = cwd };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(hook.Command);
        }
        psi.Environment["DOTCODE_PROJECT_DIR"] = cwd;
        psi.Environment["CLAUDE_PROJECT_DIR"] = cwd;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (1, "", "failed to start hook");
            // Hooks that don't read stdin may exit before we finish writing: that's fine.
            try
            {
                await p.StandardInput.WriteAsync(payload).ConfigureAwait(false);
                p.StandardInput.Close();
            }
            catch (IOException) { }
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(hook.Timeout ?? 60));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try { await p.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                ProcessRunner.Kill(p);
                return (1, "", $"hook timed out after {hook.Timeout ?? 60}s: {hook.Command}");
            }
            return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (1, "", $"hook failed: {ex.Message}");
        }
    }

    private static void Apply(HookOutcome outcome, string eventName, (int Code, string Stdout, string Stderr) r)
    {
        if (r.Code == 2)
        {
            outcome.Block = true;
            outcome.Reason = Join(outcome.Reason, r.Stderr.Trim().Length > 0 ? r.Stderr.Trim() : "Blocked by hook");
            return;
        }
        var stdout = r.Stdout.Trim();
        if (r.Code != 0)
        {
            if (r.Stderr.Trim().Length > 0) outcome.SystemMessages.Add($"{eventName} hook error: {r.Stderr.Trim()}");
            return;
        }
        if (stdout.StartsWith('{'))
        {
            try
            {
                var json = DotCodeJson.Parse(stdout);
                if (json.GetBool("continue") == false)
                {
                    outcome.StopAgent = true;
                    outcome.StopReason = json.GetString("stopReason");
                }
                if (json.GetString("systemMessage") is { } sm) outcome.SystemMessages.Add(sm);
                if (json.GetString("decision") is "block")
                {
                    outcome.Block = true;
                    outcome.Reason = Join(outcome.Reason, json.GetString("reason") ?? "Blocked by hook");
                }
                if (json.GetProp("hookSpecificOutput") is { } hso)
                {
                    if (hso.GetString("permissionDecision") is { } pd)
                    {
                        outcome.PermissionDecision = pd;
                        if (pd == "deny") { outcome.Block = true; outcome.Reason = Join(outcome.Reason, hso.GetString("permissionDecisionReason") ?? "Denied by hook"); }
                    }
                    if (hso.GetProp("updatedInput") is { } ui) outcome.UpdatedInput = ui.Clone();
                    if (hso.GetString("additionalContext") is { Length: > 0 } ac) outcome.AdditionalContext.Add(ac);
                }
                return;
            }
            catch (JsonException) { }
        }
        // Plain stdout of UserPromptSubmit / SessionStart hooks is added as context.
        if (stdout.Length > 0 && eventName is HookEvents.UserPromptSubmit or HookEvents.SessionStart)
            outcome.AdditionalContext.Add(stdout);
    }

    private static string Join(string? a, string b) => a is null ? b : a + "\n" + b;
}
