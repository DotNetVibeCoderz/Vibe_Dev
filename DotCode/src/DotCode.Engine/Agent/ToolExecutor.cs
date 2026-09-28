using System.Diagnostics;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;
using DotCode.Engine.Hooks;
using DotCode.Engine.Permissions;
using DotCode.Engine.Tools;
using DotCode.Engine.Util;

namespace DotCode.Engine.Agent;

/// <summary>Executes the tool calls of one assistant message. Consecutive concurrency-safe calls run in parallel
/// (bounded), others serially; results keep the original order. Each call goes through validation → PreToolUse hooks
/// → permission engine (+ user prompt) → execution → PostToolUse hooks → truncation.</summary>
public static class ToolExecutor
{
    private const int MaxParallel = 10;

    public const string RejectMessage =
        "The user doesn't want to proceed with this tool use. The tool use was rejected (eg. if it was a file edit, the new_string was NOT written to the file). STOP what you are doing and wait for the user to tell you how to proceed.";

    public static async Task<List<ContentPart>> RunAsync(AgentSession session, IReadOnlyList<ToolUsePart> calls, CancellationToken ct)
    {
        var results = new ContentPart[calls.Count];
        var i = 0;
        while (i < calls.Count)
        {
            if (IsSafe(session, calls[i]))
            {
                var j = i;
                while (j < calls.Count && IsSafe(session, calls[j])) j++;
                using var throttle = new SemaphoreSlim(MaxParallel);
                var batch = Enumerable.Range(i, j - i).Select(async k =>
                {
                    await throttle.WaitAsync(ct).ConfigureAwait(false);
                    try { results[k] = await RunOneAsync(session, calls[k], ct).ConfigureAwait(false); }
                    finally { throttle.Release(); }
                });
                await Task.WhenAll(batch).ConfigureAwait(false);
                i = j;
            }
            else
            {
                results[i] = await RunOneAsync(session, calls[i], ct).ConfigureAwait(false);
                i++;
            }
        }
        return [.. results];
    }

    private static bool IsSafe(AgentSession session, ToolUsePart call)
    {
        var tool = session.FindTool(call.Name);
        try { return tool is not null && tool.IsConcurrencySafe(call.Input); }
        catch (Exception) { return false; }
    }

    private static async Task<ContentPart> RunOneAsync(AgentSession session, ToolUsePart call, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tool = session.FindTool(call.Name);
        var input = call.Input;
        if (tool is null)
        {
            session.Emit(new ToolStartedEvent(call.Id, call.Name, call.Name, input));
            return Complete(session, call, call.Name, ToolResult.Error($"Error: No such tool available: {call.Name}"), sw);
        }

        string displayName;
        try { displayName = tool.DisplayName(input, session); }
        catch (Exception) { displayName = tool.Name; }
        session.Emit(new ToolStartedEvent(call.Id, tool.Name, displayName, input));

        if (input.GetString("_invalid_json") is { } raw)
            return Complete(session, call, tool.Name, ToolResult.Error($"InputValidationError: the tool input was not valid JSON: {TextUtil.FirstLine(raw, 200)}"), sw);

        var validation = tool.CheckRequired(input) ?? SafeValidate(tool, input, session);
        if (validation is not null) return Complete(session, call, tool.Name, ToolResult.Error(validation), sw);

        // PreToolUse hooks
        string? forced = null;
        var hooks = session.Runtime.Hooks;
        if (hooks.Has(HookEvents.PreToolUse))
        {
            var captured = input;
            var hook = await hooks.RunAsync(HookEvents.PreToolUse, tool.Name, w =>
            {
                w.WriteString("tool_name", tool.Name);
                w.WritePropertyName("tool_input");
                captured.WriteTo(w);
            }, session.Id, session.Store?.FilePath ?? "", ct).ConfigureAwait(false);
            foreach (var m in hook.SystemMessages) session.Emit(new NoticeEvent(NoticeLevel.Warning, m));
            if (hook.StopAgent) session.StopRequested = hook.StopReason ?? "Stopped by hook";
            if (hook.Block)
                return Complete(session, call, tool.Name, ToolResult.Error($"PreToolUse hook blocked this tool call: {hook.Reason}"), sw);
            if (hook.UpdatedInput is { } updated) input = updated;
            forced = hook.PermissionDecision;
        }

        // Permissions
        var check = forced == "allow" ? PermissionCheck.Allowed : session.Permissions.Evaluate(tool, input, session.Mode, session);
        if (forced == "ask" && check.Behavior == PermissionBehavior.Allow) check = check with { Behavior = PermissionBehavior.Ask };
        if (check.Behavior == PermissionBehavior.Deny)
            return Complete(session, call, tool.Name, ToolResult.Error(check.Reason ?? $"Permission to use {tool.Name} was denied."), sw);

        // Auto mode: a classifier replaces the prompt for actions it judges low-risk (explicit "ask" rules still ask).
        if (check.Behavior == PermissionBehavior.Ask && session.Mode == PermissionMode.Auto && check.MatchedRule is null && forced != "ask")
        {
            var verdict = await AutoModeClassifier.ClassifyAsync(session, tool, input, displayName, ct).ConfigureAwait(false);
            switch (verdict.Decision)
            {
                case AutoDecision.Allow:
                    check = PermissionCheck.Allowed;
                    session.Emit(new ToolProgressEvent(call.Id, $"Auto mode: allowed — {verdict.Reason}"));
                    break;
                case AutoDecision.Deny:
                    return Complete(session, call, tool.Name, ToolResult.Error(
                        $"Auto mode blocked this action: {verdict.Reason}\nChoose a safer approach that stays within the user's request, or ask the user to approve it explicitly."), sw);
            }
        }

        if (check.Behavior == PermissionBehavior.Ask)
        {
            var (title, detail, diff) = SafeDescribe(tool, input, session);
            var request = new PermissionRequest(call.Id, tool.Name, displayName, input, title, detail ?? check.Reason, check.SuggestedRule, diff, session.ParentToolUseId);
            var decision = await session.RequestPermissionAsync(request, ct).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                var message = decision.Feedback is { Length: > 0 } fb && !fb.StartsWith("Permission to use", StringComparison.Ordinal)
                    ? $"{RejectMessage}\nThe user provided the following reason for the rejection: {fb}"
                    : decision.Feedback ?? RejectMessage;
                if (decision.Feedback is null || decision.Feedback.Length == 0) session.RejectedThisTurn = true;
                return Complete(session, call, tool.Name, ToolResult.Error(message), sw, rejected: true);
            }
            ApplyDecision(session, tool, input, decision);
            if (decision.UpdatedInput is { } ui) input = ui;
        }

        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(input, new ToolContext { Session = session, ToolUseId = call.Id }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = ToolResult.Error($"Error: {ex.Message}");
        }

        if (tool.Name is "WebFetch" or "WebSearch" || tool.Name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var root = session;
            while (root.Parent is not null) root = root.Parent;
            root.UntrustedContentThisTurn = true;
            session.UntrustedContentThisTurn = true;
        }

        // PostToolUse hooks
        if (hooks.Has(HookEvents.PostToolUse))
        {
            var text = result.Text;
            var captured = input;
            var hook = await hooks.RunAsync(HookEvents.PostToolUse, tool.Name, w =>
            {
                w.WriteString("tool_name", tool.Name);
                w.WritePropertyName("tool_input");
                captured.WriteTo(w);
                w.WriteStartObject("tool_response");
                w.WriteString("output", text.Length > 20_000 ? text[..20_000] : text);
                w.WriteBoolean("is_error", result.IsError);
                w.WriteEndObject();
            }, session.Id, session.Store?.FilePath ?? "", ct).ConfigureAwait(false);
            foreach (var m in hook.SystemMessages) session.Emit(new NoticeEvent(NoticeLevel.Warning, m));
            if (hook.StopAgent) session.StopRequested = hook.StopReason ?? "Stopped by hook";
            var extra = new List<string>();
            if (hook.Block && hook.Reason is { } reason) extra.Add($"PostToolUse hook feedback:\n{reason}");
            extra.AddRange(hook.AdditionalContext);
            if (extra.Count > 0)
                result = new ToolResult
                {
                    Content = [.. result.Content, new TextPart("\n<system-reminder>\n" + string.Join("\n", extra) + "\n</system-reminder>")],
                    IsError = result.IsError,
                    Summary = result.Summary,
                    Diff = result.Diff,
                    DisplayOutput = result.DisplayOutput,
                };
        }

        return Complete(session, call, tool.Name, Truncate(tool, result), sw);
    }

    private static string? SafeValidate(Tool tool, JsonElement input, AgentSession session)
    {
        try { return tool.Validate(input, session); }
        catch (Exception ex) { return $"InputValidationError: {ex.Message}"; }
    }

    private static (string, string?, string?) SafeDescribe(Tool tool, JsonElement input, AgentSession session)
    {
        try { return tool.DescribeForPermission(input, session); }
        catch (Exception) { return (tool.Name, null, null); }
    }

    private static void ApplyDecision(AgentSession session, Tool tool, JsonElement input, PermissionDecision decision)
    {
        switch (decision.Kind)
        {
            case PermissionDecisionKind.AllowAlways when decision.Rule is { } rule:
                session.Permissions.AddSessionRule(rule);
                try { SettingsLoader.AddPermissionRule(DotCodePaths.ProjectLocalSettings(session.Runtime.ProjectRoot), "allow", rule); }
                catch (IOException) { }
                break;
            case PermissionDecisionKind.AllowSession:
                if (tool.GetPermissionTarget(input, session).Kind == PermissionKind.EditFile)
                {
                    var root = session.Parent ?? session;
                    while (root.Parent is not null) root = root.Parent;
                    root.SetMode(PermissionMode.AcceptEdits);
                }
                else if (decision.Rule is { } r) session.Permissions.AddSessionRule(r);
                break;
        }
    }

    private static ToolResult Truncate(Tool tool, ToolResult result)
    {
        var text = result.Text;
        if (text.Length <= tool.MaxResultChars) return result;
        string? saved = null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "dotcode-tool-results");
            Directory.CreateDirectory(dir);
            saved = Path.Combine(dir, $"{tool.Name}-{Guid.NewGuid().ToString("n")[..8]}.txt");
            File.WriteAllText(saved, text);
        }
        catch (IOException) { }
        var truncated = TextUtil.Truncate(text, tool.MaxResultChars) + (saved is null ? "" : $"\n\n[Full output ({text.Length:N0} chars) saved to {saved} — use Read with offset/limit or Grep to inspect it.]");
        return new ToolResult
        {
            Content = [new TextPart(truncated), .. result.Content.Where(c => c is not TextPart)],
            IsError = result.IsError,
            Summary = result.Summary,
            Diff = result.Diff,
            DisplayOutput = result.DisplayOutput,
        };
    }

    private static ContentPart Complete(AgentSession session, ToolUsePart call, string name, ToolResult result, Stopwatch sw, bool rejected = false)
    {
        var display = result.DisplayOutput ?? result.Text;
        session.Emit(new ToolCompletedEvent(call.Id, name, result.IsError, result.Summary, display.Length > 20_000 ? TextUtil.Truncate(display, 20_000) : display)
        {
            Diff = result.Diff,
            DurationMs = sw.ElapsedMilliseconds,
            Rejected = rejected,
        });
        return new ToolResultPart(call.Id, result.Content.Count > 0 ? result.Content : [new TextPart("(no output)")], result.IsError);
    }
}
