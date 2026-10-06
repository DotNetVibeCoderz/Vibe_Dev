using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Marbots.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

/// <summary>A message from an external channel, normalised.</summary>
public sealed record InboundMessage(string ConversationId, string SenderId, string? SenderName, string Text);

public sealed class ChannelAuthException(string message) : Exception(message);

/// <summary>Sends a bot's reply back to an external channel.</summary>
public interface IChannelAdapter
{
    string Kind { get; }
    Task SendAsync(ChannelConfig channel, string conversationId, string text, CancellationToken ct);
}

/// <summary>Shared helpers for adapters: secrets, API base URLs (overridable for tests), message chunking.</summary>
public sealed class ChannelContext(ISecretProvider secrets, IHttpClientFactory http)
{
    public const string HttpClientName = "marbots-channels";

    public HttpClient Http() => http.CreateClient(HttpClientName);

    public string? Secret(ChannelConfig c, string role) => c.SecretRefs.TryGetValue(role, out var name) ? secrets.Get(name) : null;

    public string RequireSecret(ChannelConfig c, string role) =>
        Secret(c, role) is { Length: > 0 } v ? v : throw new InvalidOperationException($"Channel '{c.Name}' has no '{role}' secret configured.");

    public static string Setting(ChannelConfig c, string key, string fallback) =>
        c.Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public static IEnumerable<string> Chunks(string text, int max)
    {
        if (text.Length <= max) { yield return text; yield break; }
        for (var i = 0; i < text.Length; i += max) yield return text.Substring(i, Math.Min(max, text.Length - i));
    }

    public static async Task EnsureAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"{what} returned {(int)resp.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
    }
}

/// <summary>Web chat: replies are read by the widget from the conversation's thread, nothing to push.</summary>
public sealed class WebChatAdapter : IChannelAdapter
{
    public string Kind => ChannelKinds.WebChat;
    public Task SendAsync(ChannelConfig channel, string conversationId, string text, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Generic webhook: POST {channelId, conversationId, text} to Settings["outboundUrl"].</summary>
public sealed class WebhookChannelAdapter(ChannelContext ctx) : IChannelAdapter
{
    public string Kind => ChannelKinds.Webhook;

    public async Task SendAsync(ChannelConfig channel, string conversationId, string text, CancellationToken ct)
    {
        var url = ChannelContext.Setting(channel, "outboundUrl", "");
        if (url.Length == 0) return; // inbound-only webhook channel
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new JsonObject { ["channelId"] = channel.Id, ["conversationId"] = conversationId, ["text"] = text }),
        };
        if (ctx.Secret(channel, "outboundSecret") is { Length: > 0 } s) req.Headers.Add("X-Marbots-Secret", s);
        using var resp = await ctx.Http().SendAsync(req, ct);
        await ChannelContext.EnsureAsync(resp, "Webhook", ct);
    }
}

/// <summary>Telegram Bot API (sendMessage). Inbound arrives by long polling (TelegramPoller) or the webhook endpoint.</summary>
public sealed class TelegramAdapter(ChannelContext ctx) : IChannelAdapter
{
    public string Kind => ChannelKinds.Telegram;

    public static string Api(ChannelConfig c, string token, string method) =>
        $"{ChannelContext.Setting(c, "apiBase", "https://api.telegram.org").TrimEnd('/')}/bot{token}/{method}";

    public async Task SendAsync(ChannelConfig channel, string conversationId, string text, CancellationToken ct)
    {
        var token = ctx.RequireSecret(channel, "token");
        foreach (var part in ChannelContext.Chunks(text, 4000))
        {
            using var resp = await ctx.Http().PostAsJsonAsync(Api(channel, token, "sendMessage"),
                new JsonObject { ["chat_id"] = conversationId, ["text"] = part }, ct);
            await ChannelContext.EnsureAsync(resp, "Telegram sendMessage", ct);
        }
    }

    /// <summary>Parses a Telegram update into a message (null for non-text updates).</summary>
    public static InboundMessage? Parse(JsonElement update)
    {
        if (!update.TryGetProperty("message", out var m) || !m.TryGetProperty("text", out var t)) return null;
        var chat = m.GetProperty("chat").GetProperty("id").GetRawText();
        var from = m.TryGetProperty("from", out var f) ? f : default;
        var sender = from.ValueKind == JsonValueKind.Object ? from.GetProperty("id").GetRawText() : chat;
        var name = from.ValueKind == JsonValueKind.Object && from.TryGetProperty("first_name", out var fn) ? fn.GetString() : null;
        return new InboundMessage(chat, sender, name, t.GetString() ?? "");
    }
}

/// <summary>Slack Web API (chat.postMessage). Inbound via the Events API endpoint, verified with the signing secret.</summary>
public sealed class SlackAdapter(ChannelContext ctx) : IChannelAdapter
{
    public string Kind => ChannelKinds.Slack;

    public async Task SendAsync(ChannelConfig channel, string conversationId, string text, CancellationToken ct)
    {
        var token = ctx.RequireSecret(channel, "token");
        var url = $"{ChannelContext.Setting(channel, "apiBase", "https://slack.com/api").TrimEnd('/')}/chat.postMessage";
        foreach (var part in ChannelContext.Chunks(text, 38000))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new JsonObject { ["channel"] = conversationId, ["text"] = part }),
            };
            req.Headers.Authorization = new("Bearer", token);
            using var resp = await ctx.Http().SendAsync(req, ct);
            await ChannelContext.EnsureAsync(resp, "Slack chat.postMessage", ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("Slack: " + (doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "error"));
        }
    }

    /// <summary>Verifies X-Slack-Signature (v0=HMAC-SHA256(signingSecret, "v0:{timestamp}:{body}")) and freshness.</summary>
    public static bool Verify(string signingSecret, string timestamp, string body, string signature, DateTimeOffset now)
    {
        if (!long.TryParse(timestamp, out var ts) || Math.Abs(now.ToUnixTimeSeconds() - ts) > 300) return false;
        var mac = "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}")));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(signature));
    }

    /// <summary>Parses a Slack event_callback "message" (ignores bot messages and edits).</summary>
    public static InboundMessage? Parse(JsonElement payload)
    {
        if (!payload.TryGetProperty("event", out var ev)) return null;
        if (ev.GetProperty("type").GetString() is not ("message" or "app_mention")) return null;
        if (ev.TryGetProperty("bot_id", out _) || ev.TryGetProperty("subtype", out _)) return null;
        var text = ev.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var user = ev.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
        return new InboundMessage(ev.GetProperty("channel").GetString() ?? "", user, user, text);
    }
}

/// <summary>WhatsApp Cloud API (Meta Graph). Inbound via the webhook endpoint (verify token + X-Hub-Signature-256).</summary>
public sealed class WhatsAppAdapter(ChannelContext ctx) : IChannelAdapter
{
    public string Kind => ChannelKinds.WhatsApp;

    public async Task SendAsync(ChannelConfig channel, string conversationId, string text, CancellationToken ct)
    {
        var token = ctx.RequireSecret(channel, "token");
        var phoneId = ChannelContext.Setting(channel, "phoneNumberId", "");
        if (phoneId.Length == 0) throw new InvalidOperationException("WhatsApp channel needs the phoneNumberId setting.");
        var url = $"{ChannelContext.Setting(channel, "apiBase", "https://graph.facebook.com/v21.0").TrimEnd('/')}/{phoneId}/messages";
        foreach (var part in ChannelContext.Chunks(text, 4000))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["messaging_product"] = "whatsapp", ["to"] = conversationId, ["type"] = "text",
                    ["text"] = new JsonObject { ["body"] = part },
                }),
            };
            req.Headers.Authorization = new("Bearer", token);
            using var resp = await ctx.Http().SendAsync(req, ct);
            await ChannelContext.EnsureAsync(resp, "WhatsApp messages", ct);
        }
    }

    public static bool VerifySignature(string appSecret, string body, string? header)
    {
        if (string.IsNullOrEmpty(header) || !header.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        var mac = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(body)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(header[7..].ToLowerInvariant()), Encoding.ASCII.GetBytes(mac));
    }

    /// <summary>All text messages in a webhook payload.</summary>
    public static IEnumerable<InboundMessage> Parse(JsonElement payload)
    {
        if (!payload.TryGetProperty("entry", out var entries)) yield break;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value) || !value.TryGetProperty("messages", out var messages)) continue;
                string? name = null;
                if (value.TryGetProperty("contacts", out var contacts) && contacts.GetArrayLength() > 0 &&
                    contacts[0].TryGetProperty("profile", out var prof) && prof.TryGetProperty("name", out var n)) name = n.GetString();
                foreach (var m in messages.EnumerateArray())
                {
                    if (m.GetProperty("type").GetString() != "text") continue;
                    var from = m.GetProperty("from").GetString() ?? "";
                    yield return new InboundMessage(from, from, name, m.GetProperty("text").GetProperty("body").GetString() ?? "");
                }
            }
        }
    }
}

/// <summary>Discord: replies are posted to a channel webhook (Settings["webhookUrl"]); inbound through the generic endpoint.</summary>
public sealed class DiscordAdapter(ChannelContext ctx) : IChannelAdapter
{
    public string Kind => ChannelKinds.Discord;

    public async Task SendAsync(ChannelConfig channel, string conversationId, string text, CancellationToken ct)
    {
        var url = ctx.Secret(channel, "webhookUrl") ?? ChannelContext.Setting(channel, "webhookUrl", "");
        if (url.Length == 0) throw new InvalidOperationException("Discord channel needs a webhookUrl.");
        foreach (var part in ChannelContext.Chunks(text, 1900))
        {
            using var resp = await ctx.Http().PostAsJsonAsync(url, new JsonObject { ["content"] = part, ["username"] = "Marbots" }, ct);
            await ChannelContext.EnsureAsync(resp, "Discord webhook", ct);
        }
    }
}

/// <summary>
/// Routes messages between external channels and bots. Each external conversation maps to one Marbots thread, so a
/// bot remembers the conversation; replies are pushed back when the task finishes. Channel input is untrusted: give
/// channel bots narrow permission profiles.
/// </summary>
public sealed class ChannelGateway(
    IDocumentStore<ChannelConfig> channels,
    IDocumentStore<ChannelConversation> conversations,
    IDocumentStore<TaskRecord> tasks,
    IEnumerable<IChannelAdapter> adapters,
    IServiceProvider services,
    IEventBus bus,
    LocalSecretProvider secrets,
    ILogger<ChannelGateway> log) : IHostedService
{
    private IDisposable? _subscription;
    private MarbotsEngine Engine => services.GetRequiredService<MarbotsEngine>();

    public static string ActorFor(string channelId) => "channel:" + channelId;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Subscribe(OnEvent);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ChannelConfig>> ListAsync(CancellationToken ct = default) => (await channels.ListAsync(ct)).OrderBy(c => c.Name).ToList();

    public Task<ChannelConfig?> GetAsync(string id, CancellationToken ct = default) => channels.GetAsync(id, ct);

    /// <summary>Saves a channel. Secret values (by role) are stored encrypted and only referenced from the config.</summary>
    public async Task<ChannelConfig> SaveAsync(ChannelConfig c, IReadOnlyDictionary<string, string>? secretValues = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) throw new ArgumentException("Name is required.");
        if (!ChannelKinds.All.Contains(c.Kind)) throw new ArgumentException($"Unknown channel kind '{c.Kind}'. Use one of: {string.Join(", ", ChannelKinds.All)}.");
        if (string.IsNullOrEmpty(c.Id)) c.Id = Ids.New("chn");
        foreach (var (role, value) in secretValues ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrEmpty(value)) continue;
            var name = $"channel:{c.Id}:{role}";
            secrets.Set(name, value);
            c.SecretRefs[role] = name;
        }
        await channels.UpsertAsync(c, ct);
        return c;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var c = await channels.GetAsync(id, ct);
        if (c is null) return false;
        foreach (var name in c.SecretRefs.Values) secrets.Set(name, null);
        return await channels.DeleteAsync(id, ct);
    }

    public string? Secret(ChannelConfig c, string role) => c.SecretRefs.TryGetValue(role, out var name) ? secrets.Get(name) : null;

    /// <summary>Accepts a message from a channel and starts the channel's bot on it.</summary>
    public async Task<TaskRecord> ReceiveAsync(ChannelConfig channel, InboundMessage msg, CancellationToken ct = default)
    {
        if (!channel.Enabled) throw new ChannelAuthException("Channel is disabled.");
        if (channel.AllowedSenders.Count > 0 && !channel.AllowedSenders.Contains(msg.SenderId))
            throw new ChannelAuthException("Sender is not allowed on this channel.");
        var text = msg.Text.Trim();
        if (text.Length == 0) throw new ArgumentException("Empty message.");
        if (text.Length > 8000) text = text[..8000];

        var key = $"{channel.Id}|{msg.ConversationId}";
        var conv = await conversations.GetAsync(key, ct);
        var engine = Engine;
        if (conv is null || await engine.GetThreadAsync(conv.ThreadId, ct) is null)
        {
            var who = msg.SenderName ?? msg.SenderId;
            var thread = await engine.CreateThreadAsync(channel.BotId, $"{channel.Kind}: {who}", ct);
            conv = new ChannelConversation { Id = key, ChannelId = channel.Id, ConversationId = msg.ConversationId, ThreadId = thread.Id };
        }
        conv.SenderName = msg.SenderName ?? conv.SenderName;
        conv.LastMessageAt = DateTimeOffset.UtcNow;
        await conversations.UpsertAsync(conv, ct);

        var prompt = msg.SenderName is { Length: > 0 } n && channel.Kind is not ChannelKinds.WebChat ? $"{n}: {text}" : text;
        var task = await engine.SendAsync(conv.ThreadId, prompt, ActorFor(channel.Id), ct);
        channel.MessagesIn++;
        await channels.UpsertAsync(channel, ct);
        await bus.PublishAsync(new AgentEvent
        {
            Type = EventTypes.ChannelMessageReceived, ThreadId = conv.ThreadId, TaskId = task.Id, BotId = channel.BotId,
            Message = $"{channel.Kind} · {msg.SenderName ?? msg.SenderId}: {AgentRuntime.Preview(text, 100)}", Data = channel.Id,
        }, ct);
        return task;
    }

    /// <summary>The conversation's thread (web chat reads replies from it).</summary>
    public async Task<string?> ThreadForAsync(string channelId, string conversationId, CancellationToken ct = default) =>
        (await conversations.GetAsync($"{channelId}|{conversationId}", ct))?.ThreadId;

    private void OnEvent(AgentEvent e)
    {
        if (e.Type == EventTypes.TaskStateChanged && e.TaskId is not null && e.Data is "Completed" or "Failed" or "Cancelled" or "TimedOut")
            _ = Task.Run(() => DeliverAsync(e.TaskId));
        else if (e.Type == EventTypes.ApprovalRequested && e.TaskId is not null)
            _ = Task.Run(() => NotifyWaitingAsync(e.TaskId));
    }

    private async Task<(ChannelConfig Channel, ChannelConversation Conv, TaskRecord Task)?> ResolveAsync(string taskId)
    {
        var task = await tasks.GetAsync(taskId);
        if (task is null) return null;
        var root = task.Depth == 0 ? task : await tasks.GetAsync(task.RootTaskId);
        if (root is null || !root.AssignedBy.StartsWith("channel:", StringComparison.Ordinal)) return null;
        var channel = await channels.GetAsync(root.AssignedBy[8..]);
        if (channel is null) return null;
        var conv = (await conversations.ListAsync()).FirstOrDefault(c => c.ChannelId == channel.Id && c.ThreadId == root.ThreadId);
        return conv is null ? null : (channel, conv, root);
    }

    private async Task DeliverAsync(string taskId)
    {
        try
        {
            var r = await ResolveAsync(taskId);
            if (r is not { } x || x.Task.Id != taskId) return; // only root tasks reply
            var text = x.Task.State == TaskState.Completed ? x.Task.Result ?? "" : $"Sorry, I could not finish that ({x.Task.State}).";
            if (text.Length == 0) return;
            await SendAsync(x.Channel, x.Conv.ConversationId, text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Channel delivery failed for task {Task}", taskId);
        }
    }

    private async Task NotifyWaitingAsync(string taskId)
    {
        try
        {
            if (await ResolveAsync(taskId) is { } x && x.Channel.Kind != ChannelKinds.WebChat)
                await SendAsync(x.Channel, x.Conv.ConversationId, "⏳ Waiting for an operator to approve the next step…");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogDebug(ex, "Channel notice failed");
        }
    }

    public async Task SendAsync(ChannelConfig channel, string conversationId, string text, CancellationToken ct = default)
    {
        var adapter = adapters.FirstOrDefault(a => a.Kind == channel.Kind) ?? throw new InvalidOperationException($"No adapter for '{channel.Kind}'.");
        try
        {
            await adapter.SendAsync(channel, conversationId, text, ct);
            channel.MessagesOut++;
            channel.LastError = null;
            await bus.PublishAsync(new AgentEvent { Type = EventTypes.ChannelMessageSent, BotId = channel.BotId, Message = $"{channel.Kind}: {AgentRuntime.Preview(text, 100)}", Data = channel.Id }, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            channel.LastError = ex.Message;
            throw;
        }
        finally
        {
            await channels.UpsertAsync(channel, CancellationToken.None);
        }
    }
}

/// <summary>Long-polls Telegram getUpdates for every enabled Telegram channel that has no webhook configured.</summary>
public sealed class TelegramPoller(IDocumentStore<ChannelConfig> channels, ChannelGateway gateway, ChannelContext ctx, ILogger<TelegramPoller> log) : BackgroundService
{
    private readonly Dictionary<string, long> _offsets = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var active = (await channels.ListAsync(stoppingToken))
                .Where(c => c.Enabled && c.Kind == ChannelKinds.Telegram && ChannelContext.Setting(c, "mode", "polling") == "polling").ToList();
            if (active.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }
            await Task.WhenAll(active.Select(c => PollOnceAsync(c, stoppingToken)));
        }
    }

    private async Task PollOnceAsync(ChannelConfig c, CancellationToken ct)
    {
        try
        {
            var token = ctx.Secret(c, "token");
            if (string.IsNullOrEmpty(token)) { await Task.Delay(TimeSpan.FromSeconds(10), ct); return; }
            var offset = _offsets.GetValueOrDefault(c.Id);
            using var resp = await ctx.Http().GetAsync(TelegramAdapter.Api(c, token, $"getUpdates?timeout=25&offset={offset}"), ct);
            await ChannelContext.EnsureAsync(resp, "Telegram getUpdates", ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var u in doc.RootElement.GetProperty("result").EnumerateArray())
            {
                _offsets[c.Id] = u.GetProperty("update_id").GetInt64() + 1;
                if (TelegramAdapter.Parse(u) is { } msg)
                {
                    try { await gateway.ReceiveAsync(c, msg, ct); }
                    catch (ChannelAuthException ex) { log.LogInformation("Telegram message ignored: {Reason}", ex.Message); }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            log.LogWarning("Telegram polling for {Channel} failed: {Error}", c.Name, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }
}
