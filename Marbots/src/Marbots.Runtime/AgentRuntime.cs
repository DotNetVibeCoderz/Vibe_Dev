using System.Text.Json;
using Marbots.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

public sealed class AgentRunRequest
{
    public required BotDefinition Bot { get; init; }
    public required TaskRecord Task { get; init; }
    /// <summary>The user-visible thread this work belongs to (root thread for delegated tasks).</summary>
    public required string ThreadId { get; init; }
    /// <summary>History for conversational runs; null for delegated runs which start from the objective only.</summary>
    public ChatThread? ConversationThread { get; init; }
    public string? Input { get; init; }
    public required string Workspace { get; init; }
}

public sealed record AgentRunResult(bool Success, string Output);

/// <summary>Assembles the tool set a bot may use for a run: kernel packs, skills, agent tools and MCP tools.</summary>
public sealed class ToolAssembler(IEnumerable<IKernelFunction> functions, McpManager mcp, IDocumentStore<McpServerConfig> mcpStore, MarbotsOptions options, ILogger<ToolAssembler> log,
    PlacementService placement, HostRegistry hostRegistry, HostConnectionManager hostConnections, SkillRegistry skills)
{
    public async Task<(Dictionary<string, IKernelFunction> Tools, List<string> Notes)> ForBotAsync(BotDefinition bot, TaskRecord task, string workspace, bool hasSkills, CancellationToken ct)
    {
        var tools = new Dictionary<string, IKernelFunction>(StringComparer.Ordinal);
        var notes = new List<string>();
        var packs = new HashSet<string>(bot.KernelFunctions, StringComparer.OrdinalIgnoreCase);
        if (hasSkills) packs.Add("skills");
        if (task.Depth >= options.MaxDelegationDepth) { packs.Remove("agents"); packs.Remove(Packs.Subagents); }
        foreach (var f in functions)
            if (packs.Contains(f.Descriptor.Pack)) tools[f.Descriptor.Name] = f;

        // Bots placed on a remote host run their environment tools (files, shell, desktop) there.
        var hostId = await placement.ResolveAsync(bot, task.ThreadId, ct);
        if (hostId != WellKnown.LocalHostId)
        {
            var host = await hostRegistry.GetAsync(hostId, ct);
            var hostName = host?.Name ?? hostId;
            await placement.MarkUsedAsync(task.ThreadId, hostId, ct);
            foreach (var (name, fn) in tools.ToList())
                if (HostProtocol.RemotePacks.Contains(fn.Descriptor.Pack)) tools[name] = new RemoteFunction(fn, hostId, hostName, hostConnections);
                else if (name == "load_skill") tools[name] = new RemoteSkillLoader(fn, skills, hostId, hostConnections);
            var hello = hostConnections.HelloOf(hostId) ?? host?.LastHello;
            notes.Add($"Your files, shell and desktop tools run on the computer '{hostName}' ({hello?.Os ?? "remote host"}{(hello is { Capabilities.Count: > 0 } ? "; has " + string.Join(", ", hello.Capabilities) : "")})" +
                (hostConnections.IsOnline(hostId) ? "." : ", which is OFFLINE right now: those tools will fail until it reconnects."));
        }
        if (bot.Container is { Image.Length: > 0 } box)
            notes.Add($"run_shell runs inside a Linux container ({box.Image}, {box.Cpus:0.#} CPU, {box.MemoryMb} MB{(box.Network ? "" : ", no network")}) with the workspace at /workspace; use sh/bash syntax, not PowerShell. You are already inside the container: do not call docker.");

        foreach (var serverId in bot.McpServers)
        {
            var server = await mcpStore.GetAsync(serverId, ct);
            if (server is null || !server.Enabled || server.IsCatalogEntry)
            {
                notes.Add($"Note: MCP server '{serverId}' is not installed or disabled.");
                continue;
            }
            try
            {
                foreach (var t in await mcp.GetToolsAsync(server, workspace, ct))
                {
                    var fn = new McpToolFunction(mcp, server, t);
                    tools[fn.Descriptor.Name] = fn;
                }
            }
            catch (InvalidOperationException ex)
            {
                log.LogWarning("MCP {Server} unavailable: {Error}", serverId, ex.Message);
                notes.Add($"Note: MCP server '{server.Name}' failed to start, its tools are unavailable this run ({ex.Message}).");
            }
        }
        return (tools, notes);
    }
}

/// <summary>
/// The BotAgent runtime loop: build context → invoke model → policy-checked tool calls → observe → repeat →
/// persist. Every transition is emitted as an event.
/// </summary>
public sealed class AgentRuntime(
    IServiceProvider services,
    IModelRouter router,
    IMessageStore messages,
    IDocumentStore<TaskRecord> tasks,
    IMemoryStore memory,
    IPolicyEngine policy,
    ApprovalService approvals,
    SkillRegistry skills,
    ContextManager context,
    ToolAssembler toolAssembler,
    BotRegistry registry,
    IEventBus bus,
    MarbotsOptions options,
    ILogger<AgentRuntime> log)
{
    public async Task<AgentRunResult> RunAsync(AgentRunRequest req, CancellationToken ct)
    {
        using var activity = MarbotsTelemetry.StartAgent(req.Bot, req.Task, options.TenantId);
        try
        {
            var result = await RunCoreAsync(req, ct);
            activity?.SetTag("gen_ai.usage.input_tokens", req.Task.InputTokens);
            activity?.SetTag("gen_ai.usage.output_tokens", req.Task.OutputTokens);
            activity?.SetTag("marbots.steps", req.Task.Steps);
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MarbotsTelemetry.Fail(activity, ex);
            throw;
        }
    }

    /// <summary>One model call, traced as a gen_ai "chat" span with token and latency metrics.</summary>
    private async Task<(ModelResponse Response, ModelProfile Profile)> CompleteAsync(BotDefinition bot, ModelRequest request, CancellationToken ct)
    {
        using var activity = MarbotsTelemetry.StartModelCall(bot.ModelProfile);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var (response, profile) = await router.CompleteAsync(bot.ModelProfile, request, ct);
            MarbotsTelemetry.EndModelCall(activity, profile, response.Usage, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, options.TenantId, bot.Id);
            return (response, profile);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MarbotsTelemetry.Fail(activity, ex);
            throw;
        }
    }

    private async Task<AgentRunResult> RunCoreAsync(AgentRunRequest req, CancellationToken ct)
    {
        var bot = req.Bot;
        var task = req.Task;
        var botSkills = skills.ForBot(bot);
        var (tools, notes) = await toolAssembler.ForBotAsync(bot, task, req.Workspace, botSkills.Count > 0, ct);
        var schemas = tools.Values.Select(t => new ToolSchema(t.Descriptor.Name, t.Descriptor.Description, t.Descriptor.ParametersSchema)).ToList();

        // ----- context -----
        List<ModelMessage> history;
        string? summary = null;
        if (req.ConversationThread is { } thread)
        {
            (history, thread) = await context.LoadAsync(thread, bot, ct);
            summary = thread.Summary;
        }
        else
        {
            history = [];
            if (req.Input is not null)
            {
                await AppendAsync(task.TranscriptId, "user", task.AssignedBy, req.Input, task.Id, ct: ct);
                history.Add(ModelMessage.User(req.Input));
            }
        }

        IReadOnlyList<MemoryMatch> recalled = [];
        var query = req.Input ?? task.Objective;
        if (bot.LongTermMemory && query.Length > 0)
            recalled = await memory.SearchAsync(new MemoryQuery(query, [bot.Id, WellKnown.SharedMemoryOwner], 6), ct);

        IReadOnlyList<BotDefinition>? roster = null;
        if (tools.ContainsKey("delegate_tasks"))
            roster = (await registry.ActiveAsync(ct)).Where(b => b.Id != bot.Id).ToList();

        var system = ContextManager.BuildSystemPrompt(bot, botSkills, recalled, summary, roster, notes, task.Depth > 0);
        var modelMessages = new List<ModelMessage>(history.Count + 1) { ModelMessage.System(system) };
        modelMessages.AddRange(history);

        var execCtx = new FunctionExecutionContext { Bot = bot, TaskId = task.Id, ThreadId = req.ThreadId, WorkspacePath = req.Workspace, Services = services };

        // ----- loop -----
        string? final = null;
        var emptyRetries = 0;
        for (var step = 1; step <= bot.MaxSteps && final is null; step++)
        {
            ct.ThrowIfCancellationRequested();
            task.Steps = step;
            await SetActivityAsync(task, TaskState.Running, "Thinking", req.ThreadId, EventTypes.AgentThinkingStarted, ct);

            var (response, profile) = await CompleteAsync(bot, new ModelRequest
            {
                Messages = modelMessages, Tools = schemas,
                OnTextDelta = piece => bus.PublishTransient(new AgentEvent
                {
                    Type = EventTypes.AssistantDelta, ThreadId = req.ThreadId, TaskId = task.Id, BotId = bot.Id, Message = piece, Data = step.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }),
            }, ct);
            Meter(task, response, profile);

            if (response.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(response.Content) && emptyRetries++ < 1)
                {
                    modelMessages.Add(ModelMessage.User("(Your previous reply was empty. Please continue and give your answer.)"));
                    continue;
                }
                final = response.Content?.Trim() ?? "";
                await AppendAsync(task.TranscriptId, "assistant", bot.Id, final, task.Id, ct: ct);
                break;
            }

            await AppendAsync(task.TranscriptId, "assistant", bot.Id, response.Content ?? "", task.Id, response.ToolCalls, ct: ct);
            modelMessages.Add(ModelMessage.Assistant(response.Content, response.ToolCalls));
            var images = new List<string>();
            foreach (var call in response.ToolCalls)
            {
                tools.TryGetValue(call.Name, out var fn);
                var result = await ExecuteToolAsync(call, fn, execCtx, task, req.ThreadId, ct);
                await AppendAsync(task.TranscriptId, "tool", bot.Id, result.Content, task.Id, toolCallId: call.Id, toolName: call.Name, ct: ct);
                modelMessages.Add(ModelMessage.Tool(call.Id, result.Content));
                if (result.Images is { Count: > 0 }) images.AddRange(result.Images);
            }
            if (images.Count > 0)
            {
                // Only the newest screenshots stay in context; older ones cost tokens and are out of date.
                foreach (var m in modelMessages) m.Images = null;
                modelMessages.Add(new ModelMessage { Role = "user", Content = "Images returned by the tools above (newest state):", Images = [.. images.TakeLast(2)] });
                images.Clear();
            }
            await tasks.UpsertAsync(task, ct);
        }

        if (final is null)
        {
            modelMessages.Add(ModelMessage.User("You have reached the step limit for this task. Stop using tools and summarize what you completed, where the results are, and what remains."));
            var (response, profile) = await CompleteAsync(bot, new ModelRequest { Messages = modelMessages }, ct);
            Meter(task, response, profile);
            final = (response.Content ?? "").Trim();
            if (final.Length == 0) final = "I reached the step limit before finishing.";
            await AppendAsync(task.TranscriptId, "assistant", bot.Id, final, task.Id, ct: ct);
        }
        return new AgentRunResult(true, final);
    }

    private static void Meter(TaskRecord task, ModelResponse response, ModelProfile profile)
    {
        task.Model = $"{profile.Provider}/{profile.Model}";
        task.InputTokens += response.Usage.InputTokens;
        task.OutputTokens += response.Usage.OutputTokens;
        task.CostUsd += response.Usage.InputTokens * profile.InputCostPerMTok / 1_000_000m
                        + response.Usage.OutputTokens * profile.OutputCostPerMTok / 1_000_000m;
    }

    private async Task<FunctionResult> ExecuteToolAsync(ToolCall call, IKernelFunction? fn, FunctionExecutionContext execCtx, TaskRecord task, string threadId, CancellationToken ct)
    {
        if (fn is null) return FunctionResult.Fail($"Unknown tool '{call.Name}'. Use only the tools provided.");
        JsonDocument args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
        }
        catch (JsonException ex)
        {
            return FunctionResult.Fail($"Arguments were not valid JSON: {ex.Message}");
        }
        using (args)
        {
            var d = fn.Descriptor;
            var decision = policy.Evaluate(new ActionRequest(d.Name, d.Category, d.Risk, call.Arguments), new SecurityContext(execCtx.Bot, threadId, task.Id));
            if (decision.Kind == PolicyDecisionKind.Deny)
            {
                await PublishToolAsync(EventTypes.ToolCallCompleted, task, threadId, $"{d.Name} denied: {decision.Reason}", false, ct);
                return FunctionResult.Fail($"Denied by policy: {decision.Reason}");
            }
            if (decision.Kind == PolicyDecisionKind.Ask)
            {
                await SetActivityAsync(task, TaskState.WaitingForHuman, $"Waiting for approval: {d.Name}", threadId, EventTypes.TaskProgressed, ct);
                var approval = await approvals.RequestAsync(new ApprovalRequest
                {
                    TaskId = task.Id, ThreadId = threadId, BotId = execCtx.Bot.Id, ToolName = d.Name, Arguments = call.Arguments,
                    Category = d.Category, Risk = d.Risk, Reason = decision.Reason,
                }, ct);
                await SetActivityAsync(task, TaskState.Running, "Resuming", threadId, EventTypes.TaskProgressed, ct);
                if (approval.State != ApprovalState.Approved)
                    return FunctionResult.Fail($"The user did not approve this action ({approval.State}). Choose another approach or explain what you need.");
            }

            var waiting = d.Pack is "agents" or Packs.Subagents ? TaskState.WaitingForAgent : TaskState.WaitingForTool;
            task.State = waiting;
            task.CurrentActivity = d.Name;
            await PublishToolAsync(EventTypes.ToolCallStarted, task, threadId, $"{d.Name} {Preview(call.Arguments, 160)}", null, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(d.Pack is "agents" or Packs.Subagents ? 3600 : d.TimeoutSeconds + 15));
            FunctionResult result;
            using var span = MarbotsTelemetry.StartTool(d, call.Id);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                result = await fn.InvokeAsync(new FunctionCall(call.Id, call.Name, args.RootElement), execCtx, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = FunctionResult.Fail($"Tool timed out after {d.TimeoutSeconds}s.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                log.LogWarning(ex, "Tool {Tool} threw", d.Name);
                result = FunctionResult.Fail(ex.Message);
            }
            MarbotsTelemetry.EndTool(span, d, result.Success, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, options.TenantId);
            task.State = TaskState.Running;
            await PublishToolAsync(EventTypes.ToolCallCompleted, task, threadId, $"{d.Name} → {Preview(result.Content, 200)}", result.Success, ct);
            return result;
        }
    }

    private Task PublishToolAsync(string type, TaskRecord task, string threadId, string message, bool? success, CancellationToken ct) =>
        bus.PublishAsync(new AgentEvent
        {
            Type = type, TaskId = task.Id, BotId = task.BotId, ThreadId = threadId, Message = message,
            Data = success is null ? null : success.Value ? "ok" : "error",
        }, ct).AsTask();

    private async Task SetActivityAsync(TaskRecord task, TaskState state, string activity, string threadId, string eventType, CancellationToken ct)
    {
        task.State = state;
        task.CurrentActivity = activity;
        await tasks.UpsertAsync(task, ct);
        await bus.PublishAsync(new AgentEvent { Type = eventType, TaskId = task.Id, BotId = task.BotId, ThreadId = threadId, Message = activity, Data = state.ToString() }, ct);
    }

    private async Task AppendAsync(string transcriptId, string role, string author, string content, string taskId,
        List<ToolCall>? calls = null, string? toolCallId = null, string? toolName = null, CancellationToken ct = default)
    {
        var msg = await messages.AppendAsync(new ChatMessage
        {
            ThreadId = transcriptId, Role = role, Author = author, Content = content, TaskId = taskId,
            ToolCalls = calls, ToolCallId = toolCallId, ToolName = toolName,
        }, ct);
        if (role != "tool")
            await bus.PublishAsync(new AgentEvent { Type = EventTypes.MessageAdded, ThreadId = transcriptId, TaskId = taskId, BotId = author, Data = msg.Id, Message = Preview(content, 120) }, ct);
    }

    public static string Preview(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = text.ReplaceLineEndings(" ");
        return t.Length <= max ? t : t[..max] + "…";
    }
}

internal static class ServiceProviderExtensions
{
    public static T Get<T>(this FunctionExecutionContext ctx) where T : notnull => ctx.Services.GetRequiredService<T>();
}
