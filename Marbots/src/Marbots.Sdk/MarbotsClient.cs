using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marbots.Abstractions;

namespace Marbots.Sdk;

public sealed record SendMessageResult(TaskRecord Task, ChatMessage? Reply);
public sealed record WorkspaceFile(string Path, long Size, DateTimeOffset Modified);
public sealed record ModelProfileInfo(string Name, string Provider, string Model, List<string> Fallbacks);
/// <summary>The workspace default model, the provider/model choices and the named profiles.</summary>
public sealed record ModelCatalog(string Default, List<string> Choices, List<ModelProfileInfo> Profiles);
/// <summary>A bot's model setting and the model it actually runs on.</summary>
public sealed record BotModelInfo(string BotId, string Setting, string Effective, bool UsesDefault, string? Warning);
public sealed record SystemInfo(string Product, string Version, string Credits, string CreditsEn, bool ModelConfigured, List<string> Profiles, string DataDirectory);

internal sealed record SendMessageBody(string Text, bool Wait, int TimeoutSeconds);
internal sealed record CreateThreadBody(string BotId, string? Title);
internal sealed record ScopeBody(string Scope);
internal sealed record SourceBody(string Source);
internal sealed record NameBody(string? Name);
internal sealed record ModelBody(string Model);
/// <summary>Whether approvals are skipped (dangerous mode).</summary>
public sealed record ApprovalSettings(bool DangerouslySkipApprovals);
internal sealed record DefaultModelResult(string Default);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SendMessageResult))]
[JsonSerializable(typeof(List<WorkspaceFile>))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(SendMessageBody))]
[JsonSerializable(typeof(CreateThreadBody))]
[JsonSerializable(typeof(ScopeBody))]
[JsonSerializable(typeof(SourceBody))]
[JsonSerializable(typeof(NameBody))]
[JsonSerializable(typeof(ModelBody))]
[JsonSerializable(typeof(ApprovalSettings))]
[JsonSerializable(typeof(DefaultModelResult))]
[JsonSerializable(typeof(ModelCatalog))]
[JsonSerializable(typeof(BotModelInfo))]
internal sealed partial class SdkJsonContext : JsonSerializerContext;

public sealed class MarbotsApiException(int status, string message) : Exception(message)
{
    public int StatusCode { get; } = status;
}

/// <summary>
/// Client for the Marbots REST API (<c>/api/v1</c>). Async-first, cancellable, and allocation-light
/// (source-generated JSON). Live events stream over Server-Sent Events.
/// </summary>
/// <example>
/// <code>
/// using var client = new MarbotsClient(new Uri("http://localhost:5170"));
/// var thread = await client.Threads.CreateAsync("boss-man");
/// var result = await client.Threads.SendAsync(thread.Id, "Plan a product launch", wait: true);
/// Console.WriteLine(result.Reply?.Content);
/// </code>
/// </example>
public sealed class MarbotsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    internal static readonly JsonSerializerOptions Json = CreateOptions();

    public MarbotsClient(Uri baseAddress, string? apiKey = null, HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _http.BaseAddress = baseAddress;
        if (!string.IsNullOrEmpty(apiKey)) _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        Bots = new BotsClient(this);
        Templates = new TemplatesClient(this);
        Threads = new ThreadsClient(this);
        Tasks = new TasksClient(this);
        Approvals = new ApprovalsClient(this);
        Skills = new SkillsClient(this);
        Mcp = new McpClient(this);
        Schedules = new SchedulesClient(this);
        Memory = new MemoryClient(this);
        Events = new EventsClient(this);
        Models = new ModelsClient(this);
    }

    public BotsClient Bots { get; }
    public TemplatesClient Templates { get; }
    public ThreadsClient Threads { get; }
    public TasksClient Tasks { get; }
    public ApprovalsClient Approvals { get; }
    public SkillsClient Skills { get; }
    public McpClient Mcp { get; }
    public SchedulesClient Schedules { get; }
    public MemoryClient Memory { get; }
    public EventsClient Events { get; }
    public ModelsClient Models { get; }

    public Task<SystemInfo> SystemAsync(CancellationToken ct = default) => GetAsync<SystemInfo>("api/v1/system", ct);

    public Task<List<HostInfo>> HostsAsync(CancellationToken ct = default) => GetAsync<List<HostInfo>>("api/v1/hosts", ct);

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(MarbotsJsonContext.Default, SdkJsonContext.Default) };
        o.Converters.Add(new JsonStringEnumConverter());
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return o;
    }

    internal HttpClient Http => _http;

    internal async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        return await ReadAsync<T>(resp, ct).ConfigureAwait(false);
    }

    internal async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return await ReadAsync<T>(resp, ct).ConfigureAwait(false);
    }

    internal async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureAsync(resp, ct).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        await EnsureAsync(resp, ct).ConfigureAwait(false);
        return (await resp.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false))!;
    }

    internal static async Task EnsureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var message = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) message = d.GetString()!;
            else if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) message = t.GetString()!;
        }
        catch (JsonException) { }
        throw new MarbotsApiException((int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}: {message}");
    }

    internal static string E(string s) => Uri.EscapeDataString(s);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public sealed class BotsClient(MarbotsClient c)
{
    public Task<List<BotDefinition>> ListAsync(CancellationToken ct = default) => c.GetAsync<List<BotDefinition>>("api/v1/bots", ct);
    public Task<BotDefinition> GetAsync(string idOrName, CancellationToken ct = default) => c.GetAsync<BotDefinition>($"api/v1/bots/{MarbotsClient.E(idOrName)}", ct);
    public Task<BotDefinition> CreateAsync(BotDefinition bot, CancellationToken ct = default) => c.SendAsync<BotDefinition>(HttpMethod.Post, "api/v1/bots", bot, ct);
    public Task<BotDefinition> UpdateAsync(BotDefinition bot, CancellationToken ct = default) => c.SendAsync<BotDefinition>(HttpMethod.Put, $"api/v1/bots/{MarbotsClient.E(bot.Id)}", bot, ct);
    public Task DeleteAsync(string id, CancellationToken ct = default) => c.SendAsync(HttpMethod.Delete, $"api/v1/bots/{MarbotsClient.E(id)}", null, ct);
    /// <summary>The bot's model setting and the model it effectively runs on.</summary>
    public Task<BotModelInfo> GetModelAsync(string id, CancellationToken ct = default) => c.GetAsync<BotModelInfo>($"api/v1/bots/{MarbotsClient.E(id)}/model", ct);

    /// <summary>Sets the bot's model: <see cref="ModelRef.Default"/>, <see cref="ModelRef.Of"/> or a profile name.</summary>
    public Task<BotModelInfo> SetModelAsync(string id, string model, CancellationToken ct = default) =>
        c.SendAsync<BotModelInfo>(HttpMethod.Put, $"api/v1/bots/{MarbotsClient.E(id)}/model", new ModelBody(model), ct);

    public Task PauseAsync(string id, CancellationToken ct = default) => c.SendAsync(HttpMethod.Post, $"api/v1/bots/{MarbotsClient.E(id)}/pause", null, ct);
    public Task ResumeAsync(string id, CancellationToken ct = default) => c.SendAsync(HttpMethod.Post, $"api/v1/bots/{MarbotsClient.E(id)}/resume", null, ct);
    public Task<BotDefinition> HireAsync(string templateId, string? name = null, CancellationToken ct = default) =>
        c.SendAsync<BotDefinition>(HttpMethod.Post, $"api/v1/bots/from-template/{MarbotsClient.E(templateId)}", new NameBody(name), ct);

    /// <summary>Downloads a .marbot package (secrets are never included).</summary>
    public async Task<byte[]> ExportAsync(string id, bool includeMemory = false, CancellationToken ct = default)
    {
        using var resp = await c.Http.GetAsync($"api/v1/bots/{MarbotsClient.E(id)}/export?includeMemory={includeMemory.ToString().ToLowerInvariant()}", ct).ConfigureAwait(false);
        await MarbotsClient.EnsureAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<BotDefinition> ImportAsync(Stream package, CancellationToken ct = default)
    {
        using var content = new StreamContent(package);
        content.Headers.ContentType = new("application/zip");
        using var resp = await c.Http.PostAsync("api/v1/bots/import", content, ct).ConfigureAwait(false);
        return await MarbotsClient.ReadAsync<BotDefinition>(resp, ct).ConfigureAwait(false);
    }
}

public sealed class TemplatesClient(MarbotsClient c)
{
    public Task<List<BotTemplate>> ListAsync(string? query = null, string? category = null, CancellationToken ct = default) =>
        c.GetAsync<List<BotTemplate>>($"api/v1/templates?q={MarbotsClient.E(query ?? "")}&category={MarbotsClient.E(category ?? "")}", ct);
    public Task<BotTemplate> SaveAsync(BotTemplate t, CancellationToken ct = default) => c.SendAsync<BotTemplate>(HttpMethod.Post, "api/v1/templates", t, ct);
    public Task DeleteAsync(string id, CancellationToken ct = default) => c.SendAsync(HttpMethod.Delete, $"api/v1/templates/{MarbotsClient.E(id)}", null, ct);
}

public sealed class ThreadsClient(MarbotsClient c)
{
    public Task<List<ChatThread>> ListAsync(string? botId = null, CancellationToken ct = default) =>
        c.GetAsync<List<ChatThread>>(botId is null ? "api/v1/threads" : $"api/v1/threads?botId={MarbotsClient.E(botId)}", ct);
    public Task<ChatThread> CreateAsync(string botId, string? title = null, CancellationToken ct = default) =>
        c.SendAsync<ChatThread>(HttpMethod.Post, "api/v1/threads", new CreateThreadBody(botId, title), ct);
    public Task<List<ChatMessage>> MessagesAsync(string threadId, long afterSeq = 0, CancellationToken ct = default) =>
        c.GetAsync<List<ChatMessage>>($"api/v1/threads/{MarbotsClient.E(threadId)}/messages?after={afterSeq}", ct);

    /// <summary>Sends a message. With <paramref name="wait"/> the call returns once the bot has finished.</summary>
    public Task<SendMessageResult> SendAsync(string threadId, string text, bool wait = false, int timeoutSeconds = 600, CancellationToken ct = default) =>
        c.SendAsync<SendMessageResult>(HttpMethod.Post, $"api/v1/threads/{MarbotsClient.E(threadId)}/messages", new SendMessageBody(text, wait, timeoutSeconds), ct);

    public Task<List<WorkspaceFile>> FilesAsync(string threadId, CancellationToken ct = default) =>
        c.GetAsync<List<WorkspaceFile>>($"api/v1/threads/{MarbotsClient.E(threadId)}/files", ct);

    public async Task<byte[]> DownloadAsync(string threadId, string path, CancellationToken ct = default)
    {
        using var resp = await c.Http.GetAsync($"api/v1/threads/{MarbotsClient.E(threadId)}/files/{path}", ct).ConfigureAwait(false);
        await MarbotsClient.EnsureAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> ExportTranscriptAsync(string threadId, CancellationToken ct = default)
    {
        using var resp = await c.Http.GetAsync($"api/v1/threads/{MarbotsClient.E(threadId)}/export", ct).ConfigureAwait(false);
        await MarbotsClient.EnsureAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public Task DeleteAsync(string threadId, CancellationToken ct = default) => c.SendAsync(HttpMethod.Delete, $"api/v1/threads/{MarbotsClient.E(threadId)}", null, ct);
}

public sealed class TasksClient(MarbotsClient c)
{
    public Task<List<TaskRecord>> ListAsync(string? threadId = null, CancellationToken ct = default) =>
        c.GetAsync<List<TaskRecord>>(threadId is null ? "api/v1/tasks" : $"api/v1/tasks?threadId={MarbotsClient.E(threadId)}", ct);
    public Task<TaskRecord> GetAsync(string id, CancellationToken ct = default) => c.GetAsync<TaskRecord>($"api/v1/tasks/{MarbotsClient.E(id)}", ct);
    public Task<List<ChatMessage>> TranscriptAsync(string id, CancellationToken ct = default) => c.GetAsync<List<ChatMessage>>($"api/v1/tasks/{MarbotsClient.E(id)}/transcript", ct);
    public Task CancelAsync(string id, CancellationToken ct = default) => c.SendAsync(HttpMethod.Post, $"api/v1/tasks/{MarbotsClient.E(id)}/cancel", null, ct);
    public Task<TaskRecord> RetryAsync(string id, CancellationToken ct = default) => c.SendAsync<TaskRecord>(HttpMethod.Post, $"api/v1/tasks/{MarbotsClient.E(id)}/retry", null, ct);
}

public sealed class ApprovalsClient(MarbotsClient c)
{
    /// <summary>True when approvals are skipped (dangerous mode).</summary>
    public async Task<bool> GetSkipApprovalsAsync(CancellationToken ct = default) =>
        (await c.GetAsync<ApprovalSettings>("api/v1/system/approvals", ct).ConfigureAwait(false)).DangerouslySkipApprovals;

    /// <summary>
    /// Dangerous: like <c>--dangerously-skip-permissions</c>, every action that would ask is allowed without a human.
    /// Turning it on also approves everything pending. Profile-denied actions stay denied.
    /// </summary>
    public async Task<bool> SetSkipApprovalsAsync(bool skip, CancellationToken ct = default) =>
        (await c.SendAsync<ApprovalSettings>(HttpMethod.Put, "api/v1/system/approvals", new ApprovalSettings(skip), ct).ConfigureAwait(false)).DangerouslySkipApprovals;

    public Task<List<ApprovalRequest>> PendingAsync(CancellationToken ct = default) => c.GetAsync<List<ApprovalRequest>>("api/v1/approvals?state=pending", ct);
    public Task<ApprovalRequest> ApproveAsync(string id, ApprovalScope scope = ApprovalScope.Once, CancellationToken ct = default) =>
        c.SendAsync<ApprovalRequest>(HttpMethod.Post, $"api/v1/approvals/{MarbotsClient.E(id)}/approve", new ScopeBody(scope.ToString()), ct);
    public Task<ApprovalRequest> RejectAsync(string id, CancellationToken ct = default) =>
        c.SendAsync<ApprovalRequest>(HttpMethod.Post, $"api/v1/approvals/{MarbotsClient.E(id)}/reject", null, ct);
}

public sealed class SkillsClient(MarbotsClient c)
{
    public Task<List<SkillInfo>> ListAsync(CancellationToken ct = default) => c.GetAsync<List<SkillInfo>>("api/v1/skills", ct);
    public Task<List<SkillInfo>> InstallAsync(string source, CancellationToken ct = default) => c.SendAsync<List<SkillInfo>>(HttpMethod.Post, "api/v1/skills/install", new SourceBody(source), ct);
}

public sealed class McpClient(MarbotsClient c)
{
    public Task<List<McpServerConfig>> ListAsync(CancellationToken ct = default) => c.GetAsync<List<McpServerConfig>>("api/v1/mcp", ct);
    public Task<McpServerConfig> InstallAsync(string id, CancellationToken ct = default) => c.SendAsync<McpServerConfig>(HttpMethod.Post, $"api/v1/mcp/{MarbotsClient.E(id)}/install", null, ct);
    public Task<McpServerConfig> AddAsync(McpServerConfig server, CancellationToken ct = default) => c.SendAsync<McpServerConfig>(HttpMethod.Post, "api/v1/mcp", server, ct);
}

public sealed class SchedulesClient(MarbotsClient c)
{
    public Task<List<ScheduleJob>> ListAsync(CancellationToken ct = default) => c.GetAsync<List<ScheduleJob>>("api/v1/schedules", ct);
    public Task<ScheduleJob> SaveAsync(ScheduleJob job, CancellationToken ct = default) => c.SendAsync<ScheduleJob>(HttpMethod.Post, "api/v1/schedules", job, ct);
    public Task DeleteAsync(string id, CancellationToken ct = default) => c.SendAsync(HttpMethod.Delete, $"api/v1/schedules/{MarbotsClient.E(id)}", null, ct);
    public Task<TaskRecord> RunNowAsync(string id, CancellationToken ct = default) => c.SendAsync<TaskRecord>(HttpMethod.Post, $"api/v1/schedules/{MarbotsClient.E(id)}/run", null, ct);
}

public sealed class MemoryClient(MarbotsClient c)
{
    public Task<List<MemoryRecord>> ListAsync(string owner, CancellationToken ct = default) => c.GetAsync<List<MemoryRecord>>($"api/v1/memory/{MarbotsClient.E(owner)}", ct);
    public Task<MemoryRecord> WriteAsync(MemoryRecord record, CancellationToken ct = default) => c.SendAsync<MemoryRecord>(HttpMethod.Post, "api/v1/memory", record, ct);
}

public sealed class ModelsClient(MarbotsClient c)
{
    public Task<ModelCatalog> ListAsync(CancellationToken ct = default) => c.GetAsync<ModelCatalog>("api/v1/models", ct);

    /// <summary>Changes the workspace default model used by every bot whose model is <see cref="ModelRef.Default"/>.</summary>
    public async Task<string> SetDefaultAsync(string model, CancellationToken ct = default) =>
        (await c.SendAsync<DefaultModelResult>(HttpMethod.Put, "api/v1/models/default", new ModelBody(model), ct).ConfigureAwait(false)).Default;
}

public sealed class EventsClient(MarbotsClient c)
{
    /// <summary>Streams live events (optionally scoped to a thread) via Server-Sent Events.</summary>
    public async IAsyncEnumerable<AgentEvent> StreamAsync(string? threadId = null, long? afterId = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = threadId is null ? "api/v1/events" : $"api/v1/threads/{MarbotsClient.E(threadId)}/events";
        if (afterId is not null) path += $"?after={afterId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var resp = await c.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await MarbotsClient.EnsureAsync(resp, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var evt = JsonSerializer.Deserialize(line.AsSpan(6), MarbotsJsonContext.Default.AgentEvent);
            if (evt is not null) yield return evt;
        }
    }
}
