using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Marbots.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

public sealed class TriggerAuthException(string message) : Exception(message);

/// <summary>
/// Starts bots without a person typing: webhook triggers (POST /api/v1/hooks/{id}, authenticated with a secret or an
/// HMAC signature) and event triggers (e.g. "when Atlas completes a task, ask Wren to summarise it").
/// Loop guards: a trigger never reacts to tasks it started itself, and fires at most 10 times per minute.
/// </summary>
public sealed class TriggerService(
    IDocumentStore<TriggerConfig> store,
    IDocumentStore<TaskRecord> tasks,
    IServiceProvider services,
    IEventBus bus,
    ISecretProvider secrets,
    ILogger<TriggerService> log) : IHostedService
{
    private const int MaxFiresPerMinute = 10;
    private ImmutableArray<TriggerConfig> _eventTriggers = [];
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _fires = new();
    private IDisposable? _subscription;

    private MarbotsEngine Engine => services.GetRequiredService<MarbotsEngine>();

    public static string ActorFor(string triggerId) => "trigger:" + triggerId;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        _subscription = bus.Subscribe(OnEvent);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task RefreshAsync(CancellationToken ct) =>
        _eventTriggers = [.. (await store.ListAsync(ct)).Where(t => t.Enabled && t.Kind == TriggerKinds.Event)];

    public async Task<IReadOnlyList<TriggerConfig>> ListAsync(CancellationToken ct = default) =>
        (await store.ListAsync(ct)).OrderBy(t => t.Name).ToList();

    public Task<TriggerConfig?> GetAsync(string id, CancellationToken ct = default) => store.GetAsync(id, ct);

    public async Task<TriggerConfig> SaveAsync(TriggerConfig t, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(t.Name)) throw new ArgumentException("Name is required.");
        if (string.IsNullOrWhiteSpace(t.PromptTemplate)) throw new ArgumentException("Prompt template is required.");
        if (t.Kind is not (TriggerKinds.Webhook or TriggerKinds.Event)) throw new ArgumentException($"Unknown trigger kind '{t.Kind}'.");
        if (t.Kind == TriggerKinds.Webhook && string.IsNullOrWhiteSpace(t.SecretRef))
            throw new ArgumentException("Webhook triggers need a secret (SecretRef) so that only callers who know it can start a bot.");
        if (string.IsNullOrEmpty(t.Id)) t.Id = Ids.New("trg");
        await store.UpsertAsync(t, ct);
        await RefreshAsync(ct);
        return t;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var ok = await store.DeleteAsync(id, ct);
        await RefreshAsync(ct);
        return ok;
    }

    /// <summary>Runs a webhook trigger. <paramref name="secret"/> or <paramref name="signature"/> (sha256=HMAC hex) must match.</summary>
    public async Task<TaskRecord> FireWebhookAsync(string id, string body, string? secret, string? signature, CancellationToken ct)
    {
        var t = await store.GetAsync(id, ct);
        if (t is null || !t.Enabled || t.Kind != TriggerKinds.Webhook) throw new TriggerAuthException("Unknown or disabled trigger.");
        var expected = t.SecretRef is null ? null : secrets.Get(t.SecretRef);
        if (string.IsNullOrEmpty(expected) || !Verify(expected, body, secret, signature))
            throw new TriggerAuthException("Invalid trigger secret or signature.");
        return await FireAsync(t, Render(t.PromptTemplate, payload: body), ct);
    }

    public static bool Verify(string expected, string body, string? secret, string? signature)
    {
        if (!string.IsNullOrEmpty(secret) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(expected)))
            return true;
        if (string.IsNullOrEmpty(signature)) return false;
        var hex = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? signature[7..] : signature;
        var mac = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(body)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hex.ToLowerInvariant()), Encoding.ASCII.GetBytes(mac));
    }

    public static string Render(string template, string? payload = null, string? bot = null, string? result = null, string? task = null) =>
        template.Replace("{{payload}}", payload ?? "", StringComparison.Ordinal)
            .Replace("{{bot}}", bot ?? "", StringComparison.Ordinal)
            .Replace("{{result}}", result ?? "", StringComparison.Ordinal)
            .Replace("{{task}}", task ?? "", StringComparison.Ordinal);

    private void OnEvent(AgentEvent e)
    {
        if (e.Type is EventTypes.TriggerFired or EventTypes.AssistantDelta || _eventTriggers.IsEmpty) return;
        foreach (var t in _eventTriggers)
        {
            if (!string.Equals(t.EventType, e.Type, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(t.SourceBotId) && t.SourceBotId != e.BotId) continue;
            if (!string.IsNullOrEmpty(t.EventData) && t.EventData != e.Data) continue;
            _ = Task.Run(() => FireForEventAsync(t, e));
        }
    }

    private async Task FireForEventAsync(TriggerConfig t, AgentEvent e)
    {
        try
        {
            TaskRecord? source = e.TaskId is null ? null : await tasks.GetAsync(e.TaskId);
            if (source is not null)
            {
                if (source.Depth > 0) return;                                  // react to user-level work only
                if (source.AssignedBy == ActorFor(t.Id)) return;               // never react to itself
            }
            var prompt = Render(t.PromptTemplate, payload: e.Message, bot: e.BotId, result: source?.Result, task: source?.Objective);
            await FireAsync(t, prompt, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Trigger {Trigger} failed", t.Name);
        }
    }

    private async Task<TaskRecord> FireAsync(TriggerConfig t, string prompt, CancellationToken ct)
    {
        var window = _fires.GetOrAdd(t.Id, _ => new ConcurrentQueue<DateTimeOffset>());
        var now = DateTimeOffset.UtcNow;
        while (window.TryPeek(out var first) && now - first > TimeSpan.FromMinutes(1)) window.TryDequeue(out _);
        if (window.Count >= MaxFiresPerMinute) throw new InvalidOperationException($"Trigger '{t.Name}' fired too often (limit {MaxFiresPerMinute}/minute).");
        window.Enqueue(now);

        var engine = Engine;
        if (t.ThreadId is null || await engine.GetThreadAsync(t.ThreadId, ct) is null)
            t.ThreadId = (await engine.CreateThreadAsync(t.BotId, $"⚡ {t.Name}", ct)).Id;
        var task = await engine.SendAsync(t.ThreadId, prompt, ActorFor(t.Id), ct);
        t.FireCount++;
        t.LastFiredAt = now;
        await store.UpsertAsync(t, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.TriggerFired, BotId = t.BotId, ThreadId = t.ThreadId, TaskId = task.Id, Message = t.Name, Data = t.Id }, ct);
        return task;
    }
}
