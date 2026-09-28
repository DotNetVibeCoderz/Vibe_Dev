using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;

namespace DotCode.Providers.Testing;

/// <summary>Deterministic provider for tests, demos and SDK conformance: plays a JSON script of responses
/// (text, thinking, tool calls) and falls back to echoing the last prompt. Recorded sessions from
/// <see cref="RecordingProvider"/> use the same format, so real conversations can be replayed offline.</summary>
/// <remarks>Script format: <c>{"responses":[{"match":"optional substring","text":"...","thinking":"...",
/// "toolCalls":[{"name":"Read","input":{...}}],"usage":{"input":10,"output":5}}]}</c></remarks>
public sealed class ScriptedProvider : IModelProvider
{
    private readonly string _id;
    private readonly List<ScriptedResponse> _responses;
    private readonly Lock _gate = new();
    private int _cursor;

    public ScriptedProvider(string id, IEnumerable<ScriptedResponse>? responses = null)
    {
        _id = id;
        _responses = responses?.ToList() ?? [];
    }

    public static ScriptedProvider FromFile(string id, string path) => new(id, ParseScript(File.ReadAllText(path)));

    public string Id => _id;
    public int Delay { get; init; }

    public ModelCapabilities GetCapabilities(string modelId) => new() { ContextWindow = 200_000, MaxOutputTokens = 32_000, Vision = true };

    public ValueTask<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo(_id, "scripted"), new ModelInfo(_id, "echo")]);

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var last = request.Messages.LastOrDefault();
        var lastText = last?.Text ?? "";
        ScriptedResponse? response = null;
        lock (_gate)
        {
            for (var i = _cursor; i < _responses.Count; i++)
            {
                var r = _responses[i];
                if (r.Match is null || lastText.Contains(r.Match, StringComparison.OrdinalIgnoreCase))
                {
                    response = r;
                    _cursor = i + 1;
                    break;
                }
            }
        }
        response ??= last?.HasToolResults == true
            ? new ScriptedResponse { Text = "Done." }
            : new ScriptedResponse { Text = "Echo: " + lastText };

        yield return new MessageStarted("msg_" + Guid.NewGuid().ToString("n")[..12], request.Model);
        if (response.Thinking is { Length: > 0 } th)
        {
            yield return new ThinkingDelta(th);
            yield return new ContentBlockCompleted(new ThinkingPart(th));
        }
        if (response.Text is { Length: > 0 } text)
        {
            for (var i = 0; i < text.Length; i += 16)
            {
                if (Delay > 0) await Task.Delay(Delay, ct).ConfigureAwait(false);
                yield return new TextDelta(text.Substring(i, Math.Min(16, text.Length - i)));
            }
            yield return new ContentBlockCompleted(new TextPart(text));
        }
        foreach (var call in response.ToolCalls)
        {
            var callId = "toolu_" + Guid.NewGuid().ToString("n")[..20];
            yield return new ToolUseStarted(callId, call.Name);
            yield return new ContentBlockCompleted(new ToolUsePart(callId, call.Name, call.Input));
        }
        var usage = new Usage
        {
            InputTokens = response.InputTokens ?? EstimateTokens(request),
            OutputTokens = response.OutputTokens ?? Math.Max(1, (response.Text?.Length ?? 0) / 4),
        };
        yield return new UsageUpdated(usage);
        yield return new MessageStopped(response.ToolCalls.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn, usage);
    }

    private static long EstimateTokens(ModelRequest r) =>
        (r.System.Sum(s => s.Text.Length) + r.Messages.Sum(m => m.Content.Sum(c => c switch
        {
            TextPart t => t.Text.Length,
            ToolResultPart tr => tr.TextContent.Length,
            ToolUsePart tu => tu.Input.GetRawText().Length,
            _ => 100,
        }))) / 4;

    public static List<ScriptedResponse> ParseScript(string json)
    {
        var root = DotCodeJson.Parse(json);
        var list = new List<ScriptedResponse>();
        var arr = root.ValueKind == JsonValueKind.Array ? root : root.GetProp("responses") ?? default;
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in arr.EnumerateArray())
        {
            var resp = new ScriptedResponse
            {
                Match = r.GetString("match"),
                Text = r.GetString("text"),
                Thinking = r.GetString("thinking"),
                InputTokens = r.GetProp("usage")?.GetInt("input"),
                OutputTokens = r.GetProp("usage")?.GetInt("output"),
            };
            if (r.GetProp("toolCalls") is { ValueKind: JsonValueKind.Array } calls)
                foreach (var c in calls.EnumerateArray())
                    resp.ToolCalls.Add(new ScriptedToolCall(c.GetString("name") ?? "", c.GetProp("input")?.Clone() ?? DotCodeJson.EmptyObject));
            list.Add(resp);
        }
        return list;
    }
}

public sealed class ScriptedResponse
{
    public string? Match { get; init; }
    public string? Text { get; init; }
    public string? Thinking { get; init; }
    public List<ScriptedToolCall> ToolCalls { get; init; } = [];
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
}

public sealed record ScriptedToolCall(string Name, JsonElement Input);

/// <summary>Wraps a real provider and appends every completed response to a script file that
/// <see cref="ScriptedProvider"/> can replay deterministically (record/replay testing without network or cost).</summary>
public sealed class RecordingProvider(IModelProvider inner, string scriptPath) : IModelProvider
{
    private static readonly Lock FileGate = new();
    public string Id => inner.Id;
    public ModelCapabilities GetCapabilities(string modelId) => inner.GetCapabilities(modelId);
    public ValueTask<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => inner.ListModelsAsync(ct);

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var text = new StringBuilder();
        string? thinking = null;
        var calls = new List<ToolUsePart>();
        Usage? usage = null;
        await foreach (var ev in inner.StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (ev)
            {
                case ContentBlockCompleted { Part: TextPart t }: text.Append(t.Text); break;
                case ContentBlockCompleted { Part: ThinkingPart th }: thinking = th.Text; break;
                case ContentBlockCompleted { Part: ToolUsePart tu }: calls.Add(tu); break;
                case MessageStopped ms: usage = ms.Usage; break;
            }
            yield return ev;
        }
        Append(text.ToString(), thinking, calls, usage);
    }

    private void Append(string text, string? thinking, List<ToolUsePart> calls, Usage? usage)
    {
        lock (FileGate)
        {
            var existing = File.Exists(scriptPath) ? File.ReadAllText(scriptPath) : "{\"responses\":[]}";
            var root = DotCodeJson.Parse(existing);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteStartArray("responses");
                if (root.GetProp("responses") is { ValueKind: JsonValueKind.Array } arr)
                    foreach (var r in arr.EnumerateArray()) r.WriteTo(w);
                w.WriteStartObject();
                if (text.Length > 0) w.WriteString("text", text);
                if (thinking is not null) w.WriteString("thinking", thinking);
                if (calls.Count > 0)
                {
                    w.WriteStartArray("toolCalls");
                    foreach (var c in calls)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", c.Name);
                        w.WritePropertyName("input");
                        c.Input.WriteTo(w);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                if (usage is not null)
                {
                    w.WriteStartObject("usage");
                    w.WriteNumber("input", usage.InputTokens);
                    w.WriteNumber("output", usage.OutputTokens);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.WriteAllBytes(scriptPath, buffer.WrittenSpan.ToArray());
        }
    }
}
