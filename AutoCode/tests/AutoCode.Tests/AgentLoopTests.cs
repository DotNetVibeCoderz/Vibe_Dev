// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Abstractions;
using AutoCode.Core.Agents;
using AutoCode.Core.Configuration;
using AutoCode.Core.Cost;
using AutoCode.Core.Hooks;
using AutoCode.Core.Permissions;
using AutoCode.Core.Runtime;
using AutoCode.Core.Sessions;
using AutoCode.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AutoCode.Tests;

/// <summary>
/// End-to-end coverage of the loop against a scripted model, so the gather → act → verify cycle,
/// permission gating and denial feedback are all exercised without a network.
/// </summary>
public sealed class AgentLoopTests
{
    private static (AgentLoop Loop, RecordingUserInterface Ui, IChatClient Client, TestServices Services)
        Build(TempWorkspace workspace, IChatClient client, AutoCodeOptions? options = null, PermissionDecision? answer = null)
    {
        options ??= new AutoCodeOptions();
        var ui = new RecordingUserInterface(answer);
        var services = new TestServices(workspace.Root, options, ui);
        var tools = ToolRegistry.CreateDefault(options);

        var loop = new AgentLoop(new AgentLoopContext
        {
            ChatClient = client,
            Tools = tools,
            Permissions = new PermissionEngine(options),
            Hooks = new HookRunner(options),
            Services = services,
            Options = options,
            Profile = new ProviderProfile { Name = "test", Model = "test-model" },
            Cost = new CostTracker(new ProviderProfile { Name = "test", Model = "test-model" }),
            Session = new Session { WorkspaceRoot = workspace.Root },
            SystemPrompt = "You are a test agent.",
        });

        return (loop, ui, client, services);
    }

    [Fact]
    public async Task A_turn_with_no_tool_calls_completes_in_one_iteration()
    {
        using var workspace = new TempWorkspace();
        var client = new ScriptedChatClient([new TextContent("Hello there.")]);
        var (loop, ui, _, _) = Build(workspace, client);

        var result = await loop.RunTurnAsync("hi", CancellationToken.None);

        Assert.Equal(1, result.Iterations);
        Assert.Equal("Hello there.", result.FinalText);
        Assert.NotNull(ui.LastEvent<TurnCompletedEvent>());
    }

    [Fact]
    public async Task The_loop_runs_a_tool_then_continues_until_the_model_stops_calling_tools()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "file contents here");

        var client = new ScriptedChatClient(
            [new FunctionCallContent("c1", "Read", new Dictionary<string, object?> { ["file_path"] = "a.txt" })],
            [new TextContent("The file says: file contents here")]);

        var (loop, ui, _, _) = Build(workspace, client);

        var result = await loop.RunTurnAsync("what is in a.txt?", CancellationToken.None);

        Assert.Equal(2, result.Iterations);
        Assert.Contains("file contents here", result.FinalText);

        var toolEvent = ui.LastEvent<ToolCallCompletedEvent>();
        Assert.NotNull(toolEvent);
        Assert.True(toolEvent.Success);
        Assert.Equal("Read", toolEvent.ToolName);
    }

    [Fact]
    public async Task The_tool_result_is_fed_back_to_the_model()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "sentinel-value");

        var client = new ScriptedChatClient(
            [new FunctionCallContent("c1", "Read", new Dictionary<string, object?> { ["file_path"] = "a.txt" })],
            [new TextContent("done")]);

        var (loop, _, _, _) = Build(workspace, client);
        await loop.RunTurnAsync("read it", CancellationToken.None);

        var secondRequest = client.ReceivedRequests[1];
        var toolResults = secondRequest
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .ToList();

        Assert.Single(toolResults);
        Assert.Contains("sentinel-value", toolResults[0].Result?.ToString());
    }

    [Fact]
    public async Task A_denied_call_is_reported_to_the_model_so_it_can_adapt()
    {
        using var workspace = new TempWorkspace();

        var client = new ScriptedChatClient(
            [new FunctionCallContent("c1", "Write", new Dictionary<string, object?>
            {
                ["file_path"] = "out.txt",
                ["content"] = "data",
            })],
            [new TextContent("Understood, I will not write that file.")]);

        var (loop, ui, _, _) = Build(workspace, client,
            answer: new PermissionDecision(PermissionOutcome.Deny, "Do not touch that file."));

        await loop.RunTurnAsync("write out.txt", CancellationToken.None);

        Assert.Single(ui.Requests);
        Assert.Equal("Write", ui.Requests[0].ToolName);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "out.txt")));

        var results = client.ReceivedRequests[1]
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .ToList();

        Assert.Contains("declined", results[0].Result?.ToString());
        Assert.Contains("Do not touch that file.", results[0].Result?.ToString());
    }

    [Fact]
    public async Task Plan_mode_refuses_writes_without_ever_prompting()
    {
        using var workspace = new TempWorkspace();
        var options = new AutoCodeOptions { PermissionMode = PermissionMode.Plan };

        var client = new ScriptedChatClient(
            [new FunctionCallContent("c1", "Write", new Dictionary<string, object?>
            {
                ["file_path"] = "out.txt",
                ["content"] = "data",
            })],
            [new TextContent("Here is my plan instead.")]);

        var (loop, ui, _, _) = Build(workspace, client, options);

        await loop.RunTurnAsync("write out.txt", CancellationToken.None);

        Assert.Empty(ui.Requests);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "out.txt")));
        Assert.NotNull(ui.LastEvent<ToolCallDeniedEvent>());
    }

    [Fact]
    public async Task Aborting_a_permission_prompt_ends_the_turn()
    {
        using var workspace = new TempWorkspace();

        var client = new ScriptedChatClient(
            [new FunctionCallContent("c1", "Write", new Dictionary<string, object?>
            {
                ["file_path"] = "out.txt",
                ["content"] = "data",
            })]);

        var (loop, _, _, _) = Build(workspace, client,
            answer: new PermissionDecision(PermissionOutcome.Abort, "stop"));

        await Assert.ThrowsAsync<TurnAbortedException>(
            () => loop.RunTurnAsync("write it", CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_tool_is_reported_without_taking_the_loop_down()
    {
        using var workspace = new TempWorkspace();

        var client = new ScriptedChatClient(
            [new FunctionCallContent("c1", "NoSuchTool", new Dictionary<string, object?>())],
            [new TextContent("I see, that tool does not exist.")]);

        var (loop, _, _, _) = Build(workspace, client);

        var result = await loop.RunTurnAsync("use it", CancellationToken.None);

        Assert.Equal(2, result.Iterations);

        var results = client.ReceivedRequests[1]
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .ToList();

        Assert.Contains("no tool named", results[0].Result?.ToString());
    }

    [Fact]
    public async Task The_iteration_cap_stops_a_model_that_never_settles()
    {
        using var workspace = new TempWorkspace();
        var options = new AutoCodeOptions { MaxTurnIterations = 3 };

        // A client that always asks for another tool call.
        var client = new EndlessToolCallClient();

        var (loop, ui, _, _) = Build(workspace, client, options);

        var result = await loop.RunTurnAsync("loop forever", CancellationToken.None);

        Assert.Equal(3, result.Iterations);

        var notice = ui.Events.OfType<NoticeEvent>().LastOrDefault();
        Assert.NotNull(notice);
        Assert.Equal(NoticeSeverity.Warning, notice.Severity);
    }

    [Fact]
    public async Task Usage_is_accumulated_across_iterations()
    {
        using var workspace = new TempWorkspace();

        var client = new ScriptedChatClient(
            [new FunctionCallContent("c1", "List", new Dictionary<string, object?>())],
            [new TextContent("done")]);

        var (loop, ui, _, _) = Build(workspace, client);
        await loop.RunTurnAsync("look around", CancellationToken.None);

        var completed = ui.LastEvent<TurnCompletedEvent>();
        Assert.NotNull(completed);
        Assert.Equal(200, completed.InputTokens);
        Assert.Equal(40, completed.OutputTokens);
    }

    /// <summary>A model that never stops calling tools, for exercising the iteration cap.</summary>
    private sealed class EndlessToolCallClient : IChatClient
    {
        private int _n;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [
                new FunctionCallContent($"call-{_n++}", "List", new Dictionary<string, object?>())]);

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var updates = new List<ChatResponseUpdate>();

            await foreach (var u in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                updates.Add(u);

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
