// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Runtime.CompilerServices;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Configuration;
using AutoCode.Core.Permissions;
using AutoCode.Core.Runtime;
using Microsoft.Extensions.AI;

namespace AutoCode.Tests;

/// <summary>A disposable workspace directory, so file tools can be exercised for real.</summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "autocode-tests", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file still held open by the OS is not worth failing a test over.
        }
    }
}

/// <summary>Records every event and answers permission prompts from a script.</summary>
public sealed class RecordingUserInterface(PermissionDecision? answer = null) : IAgentUserInterface
{
    public List<AgentEvent> Events { get; } = [];
    public List<PermissionRequest> Requests { get; } = [];

    /// <summary>Answers returned in order; the last one repeats once exhausted.</summary>
    public List<PermissionDecision> Answers { get; } = answer is null ? [] : [answer];

    public ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken)
    {
        Events.Add(evt);
        return ValueTask.CompletedTask;
    }

    public ValueTask<PermissionDecision> RequestPermissionAsync(
        PermissionRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        var index = Math.Min(Requests.Count - 1, Answers.Count - 1);
        return ValueTask.FromResult(index >= 0 ? Answers[index] : PermissionDecision.Allow);
    }

    public T? LastEvent<T>() where T : AgentEvent => Events.OfType<T>().LastOrDefault();
}

/// <summary>
/// A chat client that replays a scripted sequence of turns.
/// This is what lets the agent loop be tested end to end without a network or an API key.
/// </summary>
public sealed class ScriptedChatClient(params IReadOnlyList<AIContent>[] turns) : IChatClient
{
    private int _turn;

    /// <summary>Messages the loop sent, so a test can assert on what the model was shown.</summary>
    public List<List<ChatMessage>> ReceivedRequests { get; } = [];

    public int TurnsConsumed => _turn;

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ReceivedRequests.Add([.. messages]);

        var contents = _turn < turns.Length ? turns[_turn] : [new TextContent("done")];
        _turn++;

        foreach (var content in contents)
            yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);

        yield return new ChatResponseUpdate(ChatRole.Assistant, [
            new UsageContent(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 })]);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(update);

        return updates.ToChatResponse();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Minimal service bag for tool tests.</summary>
public sealed class TestServices(string workspaceRoot, AutoCodeOptions? options = null, IAgentUserInterface? ui = null)
    : IAgentServices
{
    public AutoCodeOptions Options { get; } = options ?? new AutoCodeOptions();
    public string WorkspaceRoot { get; } = workspaceRoot;
    public IAgentUserInterface Ui { get; } = ui ?? new RecordingUserInterface();
    public IFileAccessTracker Files { get; } = new FileAccessTracker();
    public ITodoList Todos { get; } = new TodoList();
    public ISubagentDispatcher? Subagents { get; set; }
    public ISemanticCodeIndex? SemanticIndex { get; set; }
    public HttpClient Http { get; } = new();
}

/// <summary>Helpers for driving tools directly in a test.</summary>
public static class ToolTestExtensions
{
    public static ValueTask<ToolResult> CallAsync(
        this IAgentTool tool,
        IAgentServices services,
        string argumentsJson,
        CancellationToken cancellationToken = default) =>
        tool.InvokeAsync(
            new ToolInvocation
            {
                CallId = "test-call",
                Arguments = System.Text.Json.JsonDocument.Parse(argumentsJson).RootElement.Clone(),
                WorkspaceRoot = services.WorkspaceRoot,
                Services = services,
            },
            cancellationToken);
}
