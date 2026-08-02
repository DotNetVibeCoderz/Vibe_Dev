// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Hooks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AutoCode.Core.Agents;

/// <summary>Creates a chat client for a named provider/model pair on demand.</summary>
public delegate IChatClient ChatClientResolver(string? providerName, string? modelId);

/// <summary>
/// Runs subagents on Microsoft Agent Framework.
///
/// EN: each subagent becomes a <see cref="ChatClientAgent"/> with its own session, its own system
/// prompt and a filtered toolset — which is what "isolated execution" has to mean in practice: the
/// subagent cannot see the parent conversation, and the parent gets back only the final report.
/// Tool calls still route through <see cref="ToolExecutor"/>, so a subagent is no way around the
/// permission prompt.
/// ID: setiap subagent menjadi <see cref="ChatClientAgent"/> dengan sesi, prompt, dan daftar tool
/// sendiri. Subagent tidak bisa melihat percakapan induk, dan induk hanya menerima laporan akhir.
/// Pemanggilan tool tetap melewati <see cref="ToolExecutor"/>, sehingga izin tidak bisa dilewati.
/// </summary>
public sealed class SubagentDispatcher(
    IReadOnlyDictionary<string, AgentDefinition> definitions,
    ChatClientResolver resolveChatClient,
    IToolRegistry tools,
    Permissions.PermissionEngine permissions,
    HookRunner hooks,
    IAgentServices services) : ISubagentDispatcher
{
    public IReadOnlyCollection<string> AvailableAgents => [.. definitions.Keys];

    /// <summary>Descriptions used when rendering the subagent list into the system prompt.</summary>
    public IEnumerable<string> Describe() =>
        definitions.Values.Select(a => $"{a.Name}: {a.Description}");

    public async Task<string> RunAsync(
        string agentName,
        string prompt,
        string? description,
        CancellationToken cancellationToken)
    {
        if (!definitions.TryGetValue(agentName, out var definition))
            return $"Error: no subagent named '{agentName}'.";

        var stopwatch = Stopwatch.StartNew();
        var label = description ?? definition.Description;

        await services.Ui
            .EmitAsync(new SubagentStartedEvent(definition.Name, label), cancellationToken)
            .ConfigureAwait(false);

        // Nested dispatch is disabled: a subagent that can spawn subagents turns a bounded run into
        // an unbounded one, and the depth is never visible to the user approving it.
        var isolated = new SubagentServices(services, subagents: null);

        var executor = new ToolExecutor(tools, permissions, hooks, isolated, $"subagent:{definition.Name}");

        var visible = tools
            .Filter(definition.Tools.Count > 0 ? definition.Tools : null)
            .Where(t => t.Name != "Task")
            .ToList();

        var chatClient = resolveChatClient(definition.Provider, definition.Model)
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();

        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = definition.Name,
            Description = definition.Description,
            ChatOptions = new ChatOptions
            {
                // Agent Framework 1.16 carries agent instructions on ChatOptions rather than on the
                // agent options object itself.
                Instructions = BuildInstructions(definition),
                Tools = [.. visible.Select(t => new GatedToolFunction(t, executor))],
                ToolMode = ChatToolMode.Auto,
                MaxOutputTokens = 8_000,
            },
        });

        string report;
        try
        {
            var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            var response = await agent.RunAsync(prompt, session, cancellationToken: cancellationToken).ConfigureAwait(false);
            report = response.Text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            report = $"Subagent '{definition.Name}' failed: {ex.Message}";
        }
        finally
        {
            chatClient.Dispose();
        }

        stopwatch.Stop();

        await hooks.RunAsync(
            HookEvents.SubagentStop,
            definition.Name,
            new JsonObject { ["agent"] = definition.Name, ["elapsed_ms"] = stopwatch.ElapsedMilliseconds },
            services.WorkspaceRoot,
            cancellationToken).ConfigureAwait(false);

        await services.Ui
            .EmitAsync(new SubagentCompletedEvent(definition.Name, label, stopwatch.ElapsedMilliseconds), cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(report)
            ? $"Subagent '{definition.Name}' returned no output."
            : report;
    }

    private static string BuildInstructions(AgentDefinition definition) =>
        $"""
        {definition.Prompt}

        # Operating context

        You are running as an isolated subagent. You cannot see the conversation that dispatched you,
        and the only thing that reaches it is your final message — so that message must stand alone.

        Do the work, then report: what you found or changed, where (file paths with line numbers), and
        anything you could not complete. Do not ask questions; you will not receive an answer.
        """;
}

/// <summary>
/// Wraps a tool as an <see cref="AIFunction"/> whose invocation goes through <see cref="ToolExecutor"/>.
/// This is what lets Agent Framework drive the function-calling loop without escaping the gates.
/// </summary>
internal sealed class GatedToolFunction(IAgentTool tool, ToolExecutor executor) : AIFunction
{
    public override string Name => tool.Name;

    public override string Description => tool.Description;

    public override JsonElement JsonSchema => tool.InputSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var element = JsonSerializer.SerializeToElement(
            arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
            AIJsonUtilities.DefaultOptions);

        return await executor
            .ExecuteAsync(Guid.NewGuid().ToString("n"), tool.Name, element, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Service bag for a subagent: same workspace and UI, but its own file tracking and todo list.</summary>
internal sealed class SubagentServices(IAgentServices parent, ISubagentDispatcher? subagents) : IAgentServices
{
    public Configuration.AutoCodeOptions Options => parent.Options;

    public string WorkspaceRoot => parent.WorkspaceRoot;

    public IAgentUserInterface Ui => parent.Ui;

    // A fresh tracker: the subagent must read a file itself before it is allowed to edit it,
    // regardless of what the parent conversation happened to have open.
    public IFileAccessTracker Files { get; } = new Runtime.FileAccessTracker();

    public ITodoList Todos { get; } = new Runtime.TodoList();

    public ISubagentDispatcher? Subagents { get; } = subagents;

    public ISemanticCodeIndex? SemanticIndex => parent.SemanticIndex;

    public HttpClient Http => parent.Http;
}
