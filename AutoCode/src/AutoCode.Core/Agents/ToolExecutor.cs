// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Hooks;
using AutoCode.Core.Permissions;

namespace AutoCode.Core.Agents;

/// <summary>
/// The three gates every tool call passes through — lifecycle hooks, the permission engine, and the
/// renderer — factored out so the main loop and Agent Framework subagents cannot drift apart.
///
/// EN: this exists because "the subagent skipped the approval prompt" is exactly the class of bug
/// that duplicated gating logic produces. There is one implementation, and both callers use it.
/// ID: komponen ini ada agar logika izin tidak terduplikasi antara loop utama dan subagent, sehingga
/// tidak mungkin ada jalur yang melewati prompt persetujuan.
/// </summary>
public sealed class ToolExecutor(
    IToolRegistry tools,
    PermissionEngine permissions,
    HookRunner hooks,
    IAgentServices services,
    string sessionId)
{
    /// <summary>Runs one tool call end to end and returns the text the model should see.</summary>
    public async Task<string> ExecuteAsync(
        string callId,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!tools.TryGet(toolName, out var tool))
        {
            var available = string.Join(", ", tools.Tools.Select(t => t.Name));
            return $"Error: no tool named '{toolName}'. Available tools: {available}.";
        }

        var summary = SafeSummarize(tool, arguments);
        var stopwatch = Stopwatch.StartNew();

        var gate = await CheckAsync(tool, callId, summary, arguments, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
            return gate;

        await services.Ui
            .EmitAsync(new ToolCallStartedEvent(callId, tool.Name, summary), cancellationToken)
            .ConfigureAwait(false);

        ToolResult result;
        try
        {
            result = await tool.InvokeAsync(
                new ToolInvocation
                {
                    CallId = callId,
                    Arguments = arguments,
                    WorkspaceRoot = services.WorkspaceRoot,
                    Services = services,
                    AgentName = sessionId,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = ToolResult.Fail($"{tool.Name} threw {ex.GetType().Name}: {ex.Message}");
        }

        stopwatch.Stop();

        await services.Ui.EmitAsync(
            new ToolCallCompletedEvent(callId, tool.Name, result.IsSuccess, result.Display ?? summary, stopwatch.ElapsedMilliseconds),
            cancellationToken).ConfigureAwait(false);

        return await ApplyPostHookAsync(tool.Name, arguments, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the pre-execution gates. Returns null to proceed, or the message the model should
    /// receive in place of a result.
    /// </summary>
    private async Task<string?> CheckAsync(
        IAgentTool tool,
        string callId,
        string summary,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["session_id"] = sessionId,
            ["tool_name"] = tool.Name,
            ["tool_input"] = JsonNode.Parse(arguments.GetRawText()),
        };

        var hook = await hooks
            .RunAsync(HookEvents.PreToolUse, tool.Name, payload, services.WorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);

        if (!hook.Allowed)
        {
            await services.Ui
                .EmitAsync(new ToolCallDeniedEvent(callId, tool.Name, hook.Reason ?? "blocked by hook"), cancellationToken)
                .ConfigureAwait(false);

            return $"Blocked by a PreToolUse hook: {hook.Reason}. Do not retry this call unchanged.";
        }

        var subject = ExtractSubject(arguments);
        var check = permissions.Evaluate(tool, subject);

        if (check.Kind == PermissionCheckKind.Deny)
        {
            await services.Ui
                .EmitAsync(new ToolCallDeniedEvent(callId, tool.Name, check.Reason ?? "denied"), cancellationToken)
                .ConfigureAwait(false);

            return $"Permission denied: {check.Reason}";
        }

        if (check.Kind != PermissionCheckKind.Ask)
            return null;

        var suggested = PermissionEngine.SuggestRule(tool.Name, subject, tool.Capability);

        var decision = await services.Ui.RequestPermissionAsync(
            new PermissionRequest
            {
                ToolName = tool.Name,
                Summary = summary,
                Detail = BuildDetail(arguments),
                Capability = tool.Capability,
                SuggestedRule = suggested,
            },
            cancellationToken).ConfigureAwait(false);

        if (decision.Outcome is PermissionOutcome.AllowAlways or PermissionOutcome.DenyAlways)
            permissions.AddRule(decision.Outcome, decision.Rule ?? suggested);

        if (decision.IsAllowed)
            return null;

        var reason = decision.Reason ?? "The user declined this action.";

        await services.Ui
            .EmitAsync(new ToolCallDeniedEvent(callId, tool.Name, reason), cancellationToken)
            .ConfigureAwait(false);

        if (decision.Outcome == PermissionOutcome.Abort)
            throw new TurnAbortedException(reason);

        return $"The user declined this call. {reason} Adapt your approach — do not repeat it unchanged.";
    }

    private async Task<string> ApplyPostHookAsync(
        string toolName,
        JsonElement arguments,
        ToolResult result,
        CancellationToken cancellationToken)
    {
        if (!hooks.HasHooks(HookEvents.PostToolUse))
            return result.Content;

        var payload = new JsonObject
        {
            ["session_id"] = sessionId,
            ["tool_name"] = toolName,
            ["tool_input"] = JsonNode.Parse(arguments.GetRawText()),
            ["success"] = result.IsSuccess,
        };

        var post = await hooks
            .RunAsync(HookEvents.PostToolUse, toolName, payload, services.WorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);

        return post.AdditionalContext is { Length: > 0 } injected
            ? $"{result.Content}\n\n<hook-context>\n{injected}\n</hook-context>"
            : result.Content;
    }

    /// <summary>True when the tool may run concurrently with others in the same batch.</summary>
    public bool IsConcurrencySafe(string toolName) =>
        tools.TryGet(toolName, out var tool) && tool.IsConcurrencySafe;

    internal static string SafeSummarize(IAgentTool tool, JsonElement arguments)
    {
        try
        {
            return tool.Summarize(arguments);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return tool.Name;
        }
    }

    /// <summary>
    /// The value permission rules match against: the command for shell tools, the path for file
    /// tools, and an empty string when neither applies.
    /// </summary>
    internal static string ExtractSubject(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return "";

        foreach (var key in (ReadOnlySpan<string>)["command", "file_path", "path", "url", "pattern"])
        {
            if (arguments.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }

        return "";
    }

    /// <summary>Body shown under the approval prompt: the command, or the content being written.</summary>
    internal static string? BuildDetail(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in (ReadOnlySpan<string>)["command", "new_string", "content"])
        {
            if (arguments.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return Clip(value.GetString() ?? "", 1_200);
        }

        return null;
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n… (truncated)";
}
