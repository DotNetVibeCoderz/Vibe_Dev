using Marbots.Abstractions;

namespace Marbots.Providers;

/// <summary>
/// Deterministic offline provider. Used in tests and when no API key is configured so the UI stays usable.
/// Tests can enqueue scripted responses; otherwise it echoes a short helpful reply.
/// </summary>
public sealed class MockProvider : IModelProvider
{
    private readonly Queue<Func<ModelRequest, ModelResponse>> _script = new();
    private readonly Lock _lock = new();

    public string Name { get; init; } = "mock";
    public List<ModelRequest> Requests { get; } = [];

    /// <summary>Optional responder used when the script queue is empty (handy for concurrent multi-bot tests).</summary>
    public Func<ModelRequest, ModelResponse?>? Responder { get; set; }

    public MockProvider Enqueue(Func<ModelRequest, ModelResponse> step)
    {
        lock (_lock) _script.Enqueue(step);
        return this;
    }

    public MockProvider EnqueueText(string text) => Enqueue(_ => new ModelResponse { Content = text, Usage = new(10, 5) });

    public MockProvider EnqueueTool(string name, string argumentsJson) => Enqueue(_ => new ModelResponse
    {
        FinishReason = "tool_calls",
        ToolCalls = [new ToolCall { Id = Ids.New("call"), Name = name, Arguments = argumentsJson }],
        Usage = new(10, 5),
    });

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Func<ModelRequest, ModelResponse>? step;
        lock (_lock)
        {
            Requests.Add(request);
            _script.TryDequeue(out step);
        }
        if (step is not null) return Task.FromResult(step(request));
        if (Responder?.Invoke(request) is { } answer) return Task.FromResult(answer);

        var lastUser = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var lastTool = request.Messages.LastOrDefault()?.Role == "tool";
        var text = lastTool
            ? "Done. (offline mock model — configure a provider in Settings for real answers)"
            : $"I'm running in offline mock mode, so I can't reason about: \"{Shorten(lastUser)}\". Add an API key under Settings → Model providers to bring me to life.";
        return Task.FromResult(new ModelResponse { Content = text, Model = "mock", Usage = new(lastUser.Length / 4, text.Length / 4) });
    }

    private static string Shorten(string s) => s.Length > 120 ? s[..120] + "…" : s;
}
