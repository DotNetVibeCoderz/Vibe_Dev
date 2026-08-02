// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Runtime.CompilerServices;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Agents;
using AutoCode.Core.Configuration;
using AutoCode.Core.Cost;
using AutoCode.Core.Hooks;
using AutoCode.Core.Permissions;
using AutoCode.Core.Sessions;
using AutoCode.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AutoCode.Tests;

public sealed class ContextCompactorTests
{
    private static List<ChatMessage> Conversation(int turns)
    {
        var messages = new List<ChatMessage>();

        for (var i = 0; i < turns; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"user message {i}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"assistant reply {i}"));
        }

        return messages;
    }

    [Fact]
    public async Task A_short_conversation_is_left_alone()
    {
        var compactor = new ContextCompactor(new ScriptedChatClient([new TextContent("summary")]));

        var result = await compactor.CompactAsync(Conversation(2), CancellationToken.None);

        Assert.Null(result.Summary);
        Assert.Equal(4, result.Messages.Count);
    }

    [Fact]
    public async Task A_long_conversation_is_replaced_by_a_summary_plus_the_recent_tail()
    {
        var compactor = new ContextCompactor(new ScriptedChatClient([new TextContent("Everything that happened.")]));
        var original = Conversation(20);

        var result = await compactor.CompactAsync(original, CancellationToken.None);

        Assert.NotNull(result.Summary);
        Assert.True(result.Messages.Count < original.Count);

        // The summary leads, presented as user-supplied context the model should trust.
        Assert.Equal(ChatRole.User, result.Messages[0].Role);
        Assert.Contains("Everything that happened.", result.Messages[0].Text);

        // The most recent exchange survives verbatim — that is what the agent is working on.
        Assert.Equal("assistant reply 19", result.Messages[^1].Text);
    }

    [Fact]
    public async Task A_tool_result_is_never_orphaned_from_the_call_that_requested_it()
    {
        // An unpaired FunctionResultContent is a hard API error on every provider, so the split
        // point has to move rather than land between a call and its result.
        var messages = new List<ChatMessage>();

        for (var i = 0; i < 10; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"do thing {i}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent($"c{i}", "Read", new Dictionary<string, object?> { ["file_path"] = $"{i}.txt" })]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{i}", $"contents {i}")]));
        }

        var compactor = new ContextCompactor(new ScriptedChatClient([new TextContent("summary")]));
        var result = await compactor.CompactAsync(messages, CancellationToken.None);

        Assert.NotNull(result.Summary);

        var callIds = result.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => c.CallId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var toolResult in result.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
            Assert.Contains(toolResult.CallId, callIds);
    }

    [Fact]
    public async Task The_summary_prompt_carries_the_transcript_including_tool_activity()
    {
        var client = new ScriptedChatClient([new TextContent("summary")]);
        var compactor = new ContextCompactor(client);

        var messages = Conversation(10);
        messages.Insert(2, new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("c1", "Bash", new Dictionary<string, object?> { ["command"] = "dotnet build" })]));
        messages.Insert(3, new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "build failed: CS1002")]));

        await compactor.CompactAsync(messages, CancellationToken.None);

        var sent = string.Join("\n", client.ReceivedRequests[0].Select(m => m.Text));

        Assert.Contains("[called Bash]", sent);
        Assert.Contains("build failed: CS1002", sent);
    }
}

/// <summary>
/// Proves the loop compacts on its own once the context window fills.
///
/// EN: the contract is "compact before the next request", not "compact immediately". Utilization is
/// only known once a response reports its usage, so the check runs at the top of each iteration and
/// acts on what the previous request cost. That means compaction lands before the next request goes
/// out — within the turn when tools are being called, or at the start of the following turn.
/// ID: kontraknya adalah "padatkan sebelum request berikutnya", bukan "padatkan seketika". Pemakaian
/// token baru diketahui setelah sebuah respons melaporkannya, sehingga pemeriksaan berjalan di awal
/// tiap iterasi berdasarkan biaya request sebelumnya.
/// </summary>
public sealed class AutoCompactionTests
{
    private static (AgentLoop Loop, RecordingUserInterface Ui, CostTracker Cost) Build(
        TempWorkspace workspace,
        IChatClient client,
        IChatClient summarizer,
        int contextWindow,
        double threshold)
    {
        var options = new AutoCodeOptions { CompactionThreshold = threshold, MaxTurnIterations = 3 };
        var profile = new ProviderProfile { Name = "test", Model = "test-model", ContextWindow = contextWindow };
        var ui = new RecordingUserInterface();
        var services = new TestServices(workspace.Root, options, ui);
        var cost = new CostTracker(profile);

        var loop = new AgentLoop(new AgentLoopContext
        {
            ChatClient = client,
            Tools = ToolRegistry.CreateDefault(options),
            Permissions = new PermissionEngine(options),
            Hooks = new HookRunner(options),
            Services = services,
            Options = options,
            Profile = profile,
            Cost = cost,
            Session = new Session { WorkspaceRoot = workspace.Root },
            SystemPrompt = "test",
            Compactor = new ContextCompactor(summarizer),
        });

        return (loop, ui, cost);
    }

    [Fact]
    public async Task The_loop_compacts_mid_turn_before_the_next_request_goes_out()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "contents");

        // Iteration 1 calls a tool and reports 900 of a 1000-token window; iteration 2 must
        // therefore compact before sending anything else.
        var client = new HeavyUsageChatClient(inputTokens: 900, callToolOnFirstTurn: true);
        var summarizer = new ScriptedChatClient([new TextContent("Compacted history.")]);

        var (loop, ui, cost) = Build(workspace, client, summarizer, contextWindow: 1_000, threshold: 0.8);

        loop.ResetMessages(SeedHistory(20));

        await loop.RunTurnAsync("the newest question", CancellationToken.None);

        var compaction = ui.LastEvent<CompactionEvent>();

        Assert.NotNull(compaction);
        Assert.True(compaction.MessagesAfter < compaction.MessagesBefore);
        Assert.Contains("Compacted history.", compaction.Summary);
        Assert.True(cost.ContextUtilization >= 0.8);
    }

    [Fact]
    public async Task A_full_window_is_compacted_at_the_start_of_the_next_turn()
    {
        using var workspace = new TempWorkspace();

        var client = new HeavyUsageChatClient(inputTokens: 900);
        var summarizer = new ScriptedChatClient([new TextContent("Compacted history.")]);

        var (loop, ui, _) = Build(workspace, client, summarizer, contextWindow: 1_000, threshold: 0.8);

        loop.ResetMessages(SeedHistory(20));

        // First turn fills the window; nothing to act on yet, because usage arrives with the reply.
        await loop.RunTurnAsync("first question", CancellationToken.None);
        Assert.Null(ui.LastEvent<CompactionEvent>());

        // Second turn opens by compacting, so the request it sends fits.
        await loop.RunTurnAsync("second question", CancellationToken.None);

        var compaction = ui.LastEvent<CompactionEvent>();
        Assert.NotNull(compaction);
        Assert.True(compaction.MessagesAfter < compaction.MessagesBefore);
    }

    [Fact]
    public async Task A_short_but_enormous_conversation_warns_instead_of_failing_silently()
    {
        // A handful of very large messages fills the window with nothing safe to fold away. Left
        // unreported this becomes a context-length error the user cannot explain.
        using var workspace = new TempWorkspace();

        var client = new HeavyUsageChatClient(inputTokens: 950);
        var summarizer = new ScriptedChatClient([new TextContent("unused")]);

        var (loop, ui, _) = Build(workspace, client, summarizer, contextWindow: 1_000, threshold: 0.8);

        loop.ResetMessages(SeedHistory(2));

        await loop.RunTurnAsync("first", CancellationToken.None);
        await loop.RunTurnAsync("second", CancellationToken.None);

        Assert.Null(ui.LastEvent<CompactionEvent>());

        var warning = ui.Events.OfType<NoticeEvent>()
            .LastOrDefault(n => n.Severity == NoticeSeverity.Warning && n.Message.Contains("too short to compact"));

        Assert.NotNull(warning);
        Assert.Contains("/clear", warning.Message);
    }

    [Fact]
    public async Task The_warning_is_not_repeated_on_every_iteration()
    {
        using var workspace = new TempWorkspace();

        var client = new HeavyUsageChatClient(inputTokens: 950);
        var summarizer = new ScriptedChatClient([new TextContent("unused")]);

        var (loop, ui, _) = Build(workspace, client, summarizer, contextWindow: 1_000, threshold: 0.8);

        loop.ResetMessages(SeedHistory(2));

        await loop.RunTurnAsync("first", CancellationToken.None);
        await loop.RunTurnAsync("second", CancellationToken.None);
        await loop.RunTurnAsync("third", CancellationToken.None);
        await loop.RunTurnAsync("fourth", CancellationToken.None);

        var warnings = ui.Events.OfType<NoticeEvent>()
            .Count(n => n.Message.Contains("too short to compact"));

        Assert.Equal(1, warnings);
    }

    private static List<ChatMessage> SeedHistory(int turns) =>
        [.. Enumerable.Range(0, turns).SelectMany(i => new[]
        {
            new ChatMessage(ChatRole.User, $"earlier question {i}"),
            new ChatMessage(ChatRole.Assistant, $"earlier answer {i}"),
        })];

    [Fact]
    public async Task The_loop_leaves_a_comfortable_context_alone()
    {
        using var workspace = new TempWorkspace();

        var client = new HeavyUsageChatClient(inputTokens: 100);
        var summarizer = new ScriptedChatClient([new TextContent("should not be used")]);

        var (loop, ui, _) = Build(workspace, client, summarizer, contextWindow: 1_000, threshold: 0.8);

        loop.ResetMessages(Enumerable.Range(0, 20)
            .SelectMany(i => new[]
            {
                new ChatMessage(ChatRole.User, $"question {i}"),
                new ChatMessage(ChatRole.Assistant, $"answer {i}"),
            })
            .ToList());

        await loop.RunTurnAsync("another question", CancellationToken.None);

        Assert.Null(ui.LastEvent<CompactionEvent>());
    }

    [Fact]
    public async Task A_compaction_summary_is_recorded_on_the_session_so_history_is_not_lost()
    {
        using var workspace = new TempWorkspace();

        var client = new HeavyUsageChatClient(inputTokens: 900);
        var summarizer = new ScriptedChatClient([new TextContent("What happened earlier.")]);

        var options = new AutoCodeOptions { CompactionThreshold = 0.8, MaxTurnIterations = 2 };
        var profile = new ProviderProfile { Name = "test", Model = "m", ContextWindow = 1_000 };
        var session = new Session { WorkspaceRoot = workspace.Root };
        var ui = new RecordingUserInterface();

        var loop = new AgentLoop(new AgentLoopContext
        {
            ChatClient = client,
            Tools = ToolRegistry.CreateDefault(options),
            Permissions = new PermissionEngine(options),
            Hooks = new HookRunner(options),
            Services = new TestServices(workspace.Root, options, ui),
            Options = options,
            Profile = profile,
            Cost = new CostTracker(profile),
            Session = session,
            SystemPrompt = "test",
            Compactor = new ContextCompactor(summarizer),
        });

        loop.ResetMessages(Enumerable.Range(0, 20)
            .SelectMany(i => new[]
            {
                new ChatMessage(ChatRole.User, $"q{i}"),
                new ChatMessage(ChatRole.Assistant, $"a{i}"),
            })
            .ToList());

        await loop.RunTurnAsync("fills the window", CancellationToken.None);
        await loop.RunTurnAsync("triggers the compaction", CancellationToken.None);

        Assert.Single(session.CompactionSummaries);
        Assert.Contains("What happened earlier.", session.CompactionSummaries[0]);
    }

    /// <summary>Reports a fixed, large input-token count so the compaction threshold is crossed.</summary>
    private sealed class HeavyUsageChatClient(long inputTokens, bool callToolOnFirstTurn = false) : IChatClient
    {
        private int _requests;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = _requests++ == 0;

            if (callToolOnFirstTurn && first)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [
                    new FunctionCallContent("c1", "Read", new Dictionary<string, object?> { ["file_path"] = "a.txt" })]);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = 10,
                    TotalTokenCount = inputTokens + 10,
                })]);

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
