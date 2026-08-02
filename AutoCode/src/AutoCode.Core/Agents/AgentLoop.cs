// Auto Code â€” Gravicode Studios (Kang Fadhil)

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Configuration;
using AutoCode.Core.Cost;
using AutoCode.Core.Hooks;
using AutoCode.Core.Permissions;
using AutoCode.Core.Sessions;
using Microsoft.Extensions.AI;

namespace AutoCode.Core.Agents;

/// <summary>Everything the loop needs, gathered once at session start.</summary>
public sealed class AgentLoopContext
{
    public required IChatClient ChatClient { get; init; }
    public required IToolRegistry Tools { get; init; }
    public required PermissionEngine Permissions { get; init; }
    public required HookRunner Hooks { get; init; }
    public required IAgentServices Services { get; init; }
    public required AutoCodeOptions Options { get; init; }
    public required ProviderProfile Profile { get; init; }
    public required CostTracker Cost { get; init; }
    public required Session Session { get; init; }
    public required string SystemPrompt { get; init; }

    /// <summary>Compacts history when the window fills. Null disables auto-compaction.</summary>
    public ContextCompactor? Compactor { get; init; }

    /// <summary>Restricts the visible toolset â€” used when the loop is driving a subagent.</summary>
    public IReadOnlyCollection<string>? AllowedTools { get; init; }

    /// <summary>Caps iterations for this loop instance. Falls back to the configured limit.</summary>
    public int? MaxIterations { get; init; }
}

/// <summary>
/// The agentic loop: gather context, take action, verify results â€” repeated until the model stops
/// asking for tools.
///
/// EN: tool execution is orchestrated here rather than delegated to the chat client's automatic
/// function invocation, because every call has to pass through three gates first â€” lifecycle hooks,
/// the permission engine, and the renderer that shows the user what is about to happen. A denial is
/// fed back to the model as a tool result, so the agent can adapt instead of stalling.
/// ID: eksekusi tool diatur di sini, bukan diserahkan ke pemanggilan fungsi otomatis, karena setiap
/// pemanggilan harus melewati tiga gerbang: hook, mesin izin, dan renderer. Penolakan dikembalikan ke
/// model sebagai hasil tool sehingga agent bisa menyesuaikan diri, bukan berhenti.
/// </summary>
public sealed class AgentLoop(AgentLoopContext context)
{
    private readonly List<ChatMessage> _messages = [.. context.Session.Messages];

    private readonly ToolExecutor _executor = new(
        context.Tools, context.Permissions, context.Hooks, context.Services, context.Session.Id);

    /// <summary>Suppresses repeating the "compaction cannot help" warning on every iteration.</summary>
    private bool _warnedCompactionIneffective;

    /// <summary>The live transcript, excluding the system prompt.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Replaces the transcript, used by <c>/clear</c> and by resume.</summary>
    public void ResetMessages(IEnumerable<ChatMessage> messages)
    {
        _messages.Clear();
        _messages.AddRange(messages);
    }

    /// <summary>
    /// Runs one user turn to completion: the model may call tools as many times as it needs, and the
    /// turn ends when it produces a response with no tool calls left.
    /// </summary>
    public async Task<TurnResult> RunTurnAsync(string userMessage, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var baseline = context.Cost.Snapshot();

        var promptHook = await context.Hooks.RunAsync(
            HookEvents.UserPromptSubmit,
            null,
            new JsonObject { ["prompt"] = userMessage, ["session_id"] = context.Session.Id },
            context.Services.WorkspaceRoot,
            cancellationToken).ConfigureAwait(false);

        if (!promptHook.Allowed)
        {
            await EmitAsync(new ErrorEvent($"Prompt blocked by hook: {promptHook.Reason}"), cancellationToken).ConfigureAwait(false);
            return new TurnResult(0, "", Blocked: true);
        }

        if (promptHook.AdditionalContext is { Length: > 0 } extra)
            userMessage = $"{userMessage}\n\n<hook-context>\n{extra}\n</hook-context>";

        _messages.Add(new ChatMessage(ChatRole.User, userMessage));

        var maxIterations = context.MaxIterations ?? context.Options.MaxTurnIterations;
        var finalText = "";
        var iteration = 0;

        while (iteration < maxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iteration++;

            await EmitAsync(new TurnStartedEvent(context.Session.Id, iteration), cancellationToken).ConfigureAwait(false);
            await MaybeCompactAsync(cancellationToken).ConfigureAwait(false);

            var (assistantMessage, text) = await StreamAssistantTurnAsync(cancellationToken).ConfigureAwait(false);

            _messages.Add(assistantMessage);

            if (text.Length > 0)
                finalText = text;

            var calls = assistantMessage.Contents.OfType<FunctionCallContent>().ToList();

            if (calls.Count == 0)
                break;

            var results = await ExecuteToolCallsAsync(calls, cancellationToken).ConfigureAwait(false);
            _messages.Add(new ChatMessage(ChatRole.Tool, results));
        }

        if (iteration >= maxIterations)
        {
            await EmitAsync(
                new NoticeEvent($"Stopped after {maxIterations} iterations. Send another message to continue.",
                    NoticeSeverity.Warning),
                cancellationToken).ConfigureAwait(false);
        }

        await context.Hooks.RunAsync(
            HookEvents.Stop,
            null,
            new JsonObject { ["session_id"] = context.Session.Id, ["iterations"] = iteration },
            context.Services.WorkspaceRoot,
            cancellationToken).ConfigureAwait(false);

        PersistToSession();

        var delta = context.Cost.Snapshot().Since(baseline);

        await EmitAsync(
            new TurnCompletedEvent(iteration, delta.InputTokens, delta.OutputTokens, delta.CostUsd, stopwatch.ElapsedMilliseconds),
            cancellationToken).ConfigureAwait(false);

        return new TurnResult(iteration, finalText, Blocked: false);
    }

    /// <summary>Streams one assistant response, surfacing text and reasoning as it arrives.</summary>
    private async Task<(ChatMessage Message, string Text)> StreamAssistantTurnAsync(CancellationToken cancellationToken)
    {
        var options = BuildChatOptions();
        var updates = new List<ChatResponseUpdate>();
        var text = new System.Text.StringBuilder();

        var request = new List<ChatMessage>(_messages.Count + 1)
        {
            new(ChatRole.System, context.SystemPrompt),
        };
        request.AddRange(_messages);

        await foreach (var update in context.ChatClient
            .GetStreamingResponseAsync(request, options, cancellationToken)
            .ConfigureAwait(false))
        {
            updates.Add(update);

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } chunk:
                        text.Append(chunk.Text);
                        await EmitAsync(new AssistantTextEvent(chunk.Text), cancellationToken).ConfigureAwait(false);
                        break;

                    case TextReasoningContent { Text.Length: > 0 } reasoning when context.Options.ShowThinking:
                        await EmitAsync(new AssistantThinkingEvent(reasoning.Text), cancellationToken).ConfigureAwait(false);
                        break;

                    case UsageContent usage:
                        context.Cost.Record(usage.Details);
                        break;
                }
            }
        }

        if (text.Length > 0)
            await EmitAsync(new AssistantTextEvent("", IsFinal: true), cancellationToken).ConfigureAwait(false);

        var response = updates.ToChatResponse();
        var message = response.Messages.Count > 0
            ? MergeAssistantMessages(response.Messages)
            : new ChatMessage(ChatRole.Assistant, text.ToString());

        return (message, text.ToString());
    }

    /// <summary>
    /// Collapses the response into a single assistant message so the transcript stays flat.
    /// Providers differ in how many messages a streamed turn produces; downstream code should not care.
    /// </summary>
    private static ChatMessage MergeAssistantMessages(IList<ChatMessage> messages)
    {
        if (messages.Count == 1)
            return messages[0];

        var contents = new List<AIContent>();
        foreach (var message in messages)
            contents.AddRange(message.Contents);

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private ChatOptions BuildChatOptions()
    {
        var visible = context.Tools.Filter(context.AllowedTools);

        return new ChatOptions
        {
            ModelId = context.Profile.Model,
            Temperature = context.Profile.Temperature,
            MaxOutputTokens = context.Profile.MaxOutputTokens,
            Tools = [.. visible.Select(t => new ToolFunctionAdapter(t))],
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = context.Profile.SupportsParallelToolCalls,
        };
    }

    /// <summary>
    /// Runs the requested tool calls. Read-only calls that are safe to parallelise run concurrently;
    /// anything that mutates state runs in sequence so the user sees a coherent order of events.
    /// </summary>
    private async Task<List<AIContent>> ExecuteToolCallsAsync(
        List<FunctionCallContent> calls,
        CancellationToken cancellationToken)
    {
        var results = new List<AIContent>(calls.Count);

        var parallel = calls.Where(c => _executor.IsConcurrencySafe(c.Name)).ToList();
        var sequential = calls.Where(c => !_executor.IsConcurrencySafe(c.Name)).ToList();

        if (parallel.Count > 1)
        {
            var tasks = parallel.Select(c => ExecuteSingleAsync(c, cancellationToken)).ToArray();
            results.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        }
        else
        {
            foreach (var call in parallel)
                results.Add(await ExecuteSingleAsync(call, cancellationToken).ConfigureAwait(false));
        }

        foreach (var call in sequential)
            results.Add(await ExecuteSingleAsync(call, cancellationToken).ConfigureAwait(false));

        // Preserve the model's ordering: some providers validate that results echo the call order.
        var byId = results.OfType<FunctionResultContent>().ToDictionary(r => r.CallId, StringComparer.Ordinal);
        return [.. calls.Select(c => (AIContent)byId[c.CallId])];
    }

    private async Task<FunctionResultContent> ExecuteSingleAsync(
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        var content = await _executor
            .ExecuteAsync(call.CallId, call.Name, ToJsonElement(call.Arguments), cancellationToken)
            .ConfigureAwait(false);

        return new FunctionResultContent(call.CallId, content);
    }

    private async Task MaybeCompactAsync(CancellationToken cancellationToken)
    {
        if (context.Compactor is null || context.Cost.ContextUtilization < context.Options.CompactionThreshold)
            return;

        var before = _messages.Count;

        await context.Hooks.RunAsync(
            HookEvents.PreCompact,
            null,
            new JsonObject { ["session_id"] = context.Session.Id, ["message_count"] = before },
            context.Services.WorkspaceRoot,
            cancellationToken).ConfigureAwait(false);

        var result = await context.Compactor.CompactAsync(_messages, cancellationToken).ConfigureAwait(false);

        if (result.Summary is null)
        {
            // The window is filling but there is nothing safe to fold away — the transcript is short
            // and its messages are simply large, which a summary of the older half cannot fix.
            // Say so once, because the alternative is a context-length error the user cannot explain.
            await WarnCompactionCannotHelpAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _warnedCompactionIneffective = false;
        _messages.Clear();
        _messages.AddRange(result.Messages);
        context.Session.CompactionSummaries.Add(result.Summary);

        await EmitAsync(new CompactionEvent(before, _messages.Count, result.Summary), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Warns, at most once per run of ineffective attempts, that the context is filling and
    /// compaction cannot relieve it.
    /// </summary>
    private async Task WarnCompactionCannotHelpAsync(CancellationToken cancellationToken)
    {
        if (_warnedCompactionIneffective)
            return;

        _warnedCompactionIneffective = true;

        await EmitAsync(
            new NoticeEvent(
                $"Context is {context.Cost.ContextUtilization:P0} full but the conversation is too short to " +
                "compact — a few very large messages are filling the window. Use /clear to start fresh, " +
                "or read smaller slices of large files.",
                NoticeSeverity.Warning),
            cancellationToken).ConfigureAwait(false);
    }

    private void PersistToSession()
    {
        context.Session.Messages = [.. _messages];
        context.Session.InputTokens = context.Cost.InputTokens;
        context.Session.OutputTokens = context.Cost.OutputTokens;
        context.Session.CostUsd = context.Cost.TotalCostUsd;
        context.Session.ProviderName = context.Profile.Name;
        context.Session.ModelId = context.Profile.Model;
    }

    private ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken) =>
        context.Services.Ui.EmitAsync(evt, cancellationToken);


    private static JsonElement ToJsonElement(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return JsonDocument.Parse("{}").RootElement.Clone();

        try
        {
            return JsonSerializer.SerializeToElement(arguments, AIJsonUtilities.DefaultOptions);
        }
        catch (NotSupportedException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }
}

/// <summary>Result of one user turn.</summary>
public readonly record struct TurnResult(int Iterations, string FinalText, bool Blocked);

/// <summary>Raised when the user chooses to abort rather than approve or deny a single call.</summary>
public sealed class TurnAbortedException(string reason) : Exception(reason);
