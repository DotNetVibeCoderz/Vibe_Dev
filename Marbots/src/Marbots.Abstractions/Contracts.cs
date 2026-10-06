using System.Text.Json;

namespace Marbots.Abstractions;

// ---------- Model providers ----------

public sealed class ModelMessage
{
    public string Role { get; set; } = "user";
    public string? Content { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }

    public static ModelMessage System(string content) => new() { Role = "system", Content = content };
    public static ModelMessage User(string content) => new() { Role = "user", Content = content };
    public static ModelMessage Assistant(string? content, List<ToolCall>? calls = null) => new() { Role = "assistant", Content = content, ToolCalls = calls };
    public static ModelMessage Tool(string callId, string content) => new() { Role = "tool", ToolCallId = callId, Content = content };
}

public sealed record ToolSchema(string Name, string Description, string ParametersJson);

public sealed class ModelRequest
{
    public string Model { get; set; } = "";
    public List<ModelMessage> Messages { get; set; } = [];
    public List<ToolSchema> Tools { get; set; } = [];
    public int? MaxOutputTokens { get; set; }
}

public sealed record ModelUsage(long InputTokens, long OutputTokens);

public sealed class ModelResponse
{
    public string? Content { get; set; }
    public List<ToolCall> ToolCalls { get; set; } = [];
    public string FinishReason { get; set; } = "stop";
    public ModelUsage Usage { get; set; } = new(0, 0);
    public string Model { get; set; } = "";
}

public interface IModelProvider
{
    string Name { get; }
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>Resolves a model profile name into a completion, applying fallbacks.</summary>
public interface IModelRouter
{
    Task<(ModelResponse Response, ModelProfile Profile)> CompleteAsync(string profileName, ModelRequest request, CancellationToken cancellationToken);
    IReadOnlyList<ModelProfile> Profiles { get; }
    bool IsConfigured { get; }
}

// ---------- Kernel functions ----------

public sealed record FunctionDescriptor(
    string Name,
    string Description,
    string ParametersSchema,
    string Pack,
    PermissionCategory Category,
    RiskLevel Risk,
    int TimeoutSeconds = 120);

public sealed record FunctionCall(string Id, string Name, JsonElement Arguments)
{
    public string? GetString(string name) =>
        Arguments.ValueKind == JsonValueKind.Object && Arguments.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    public string Require(string name) =>
        GetString(name) is { Length: > 0 } s ? s : throw new ArgumentException($"Missing required argument '{name}'.");

    public int GetInt(string name, int fallback) =>
        Arguments.ValueKind == JsonValueKind.Object && Arguments.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    public bool GetBool(string name, bool fallback) =>
        Arguments.ValueKind == JsonValueKind.Object && Arguments.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;
}

public sealed record FunctionResult(bool Success, string Content)
{
    public static FunctionResult Ok(string content) => new(true, content);
    public static FunctionResult Fail(string error) => new(false, "ERROR: " + error);
}

public sealed class FunctionExecutionContext
{
    public required BotDefinition Bot { get; init; }
    public required string TaskId { get; init; }
    public required string ThreadId { get; init; }
    public required string WorkspacePath { get; init; }
    public required IServiceProvider Services { get; init; }
}

public interface IKernelFunction
{
    FunctionDescriptor Descriptor { get; }
    ValueTask<FunctionResult> InvokeAsync(FunctionCall call, FunctionExecutionContext context, CancellationToken cancellationToken);
}

// ---------- Policy ----------

public enum PolicyDecisionKind { Allow, Deny, Ask }

public sealed record PolicyDecision(PolicyDecisionKind Kind, string Reason);

public sealed record ActionRequest(string ToolName, PermissionCategory Category, RiskLevel Risk, string Arguments);

public sealed record SecurityContext(BotDefinition Bot, string ThreadId, string TaskId);

public interface IPolicyEngine
{
    PolicyDecision Evaluate(ActionRequest action, SecurityContext context);
    void GrantForSession(string threadId, string botId, string toolName);
    IReadOnlyList<string> Profiles { get; }

    /// <summary>
    /// Dangerous mode (like <c>--dangerously-skip-permissions</c>): every action that would ask for approval is allowed
    /// without asking. Actions a bot's permission profile denies stay denied.
    /// </summary>
    bool SkipApprovals { get; set; }
}

// ---------- Storage ----------

public interface IDocumentStore<T> where T : class
{
    Task<T?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(T item, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public interface IMessageStore
{
    Task<ChatMessage> AppendAsync(ChatMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> ListAsync(string threadId, long afterSeq = 0, int limit = 500, CancellationToken cancellationToken = default);
    Task<int> CountAsync(string threadId, CancellationToken cancellationToken = default);
    Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default);
}

public interface IEventStore
{
    Task<long> AppendAsync(AgentEvent evt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentEvent>> ListAsync(string? threadId, string? taskId, long afterId, int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentEvent>> RecentAsync(int limit, CancellationToken cancellationToken = default);
}

public interface IMemoryStore
{
    ValueTask WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<MemoryMatch>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<MemoryRecord>> ListAsync(string owner, int limit = 200, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
    ValueTask<int> DeleteOwnerAsync(string owner, CancellationToken cancellationToken = default);
}

public interface ISecretProvider
{
    string? Get(string name);
}

public static class Ids
{
    public static string New(string prefix) => $"{prefix}_{Guid.CreateVersion7():N}"[..(prefix.Length + 1 + 24)];

    public static string Slug(string text)
    {
        Span<char> buf = stackalloc char[Math.Min(text.Length, 48)];
        var n = 0;
        var dash = false;
        foreach (var c in text)
        {
            if (n >= buf.Length) break;
            if (char.IsAsciiLetterOrDigit(c)) { buf[n++] = char.ToLowerInvariant(c); dash = false; }
            else if (!dash && n > 0) { buf[n++] = '-'; dash = true; }
        }
        return new string(buf[..n]).Trim('-');
    }
}
