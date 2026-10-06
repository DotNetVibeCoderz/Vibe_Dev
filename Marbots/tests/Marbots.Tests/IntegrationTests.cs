using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Providers;
using Marbots.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Marbots.Tests;

/// <summary>Records outgoing channel HTTP calls and answers 200 {"ok":true}.</summary>
public sealed class FakeChannelHttp : HttpMessageHandler
{
    public ConcurrentQueue<(HttpMethod Method, string Url, string Body, string? Auth)> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Enqueue((request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true,"result":[]}""", Encoding.UTF8, "application/json") };
    }
}

/// <summary>Phase 3: suggest-mode delegation, triggers and channels (runtime level, mock model, fake channel HTTP).</summary>
public sealed class IntegrationTests : IAsyncLifetime, IDisposable
{
    public void Dispose() => _http.Dispose();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-int-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeChannelHttp _http = new();
    private ServiceProvider _sp = default!;
    private MockProvider Mock => _sp.GetRequiredService<ModelRouter>().Mock;
    private MarbotsEngine Engine => _sp.GetRequiredService<MarbotsEngine>();

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddMarbotsRuntime(new MarbotsOptions { DataDirectory = _dir, ApprovalTimeoutMinutes = 1 });
        services.AddHttpClient(ChannelContext.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _http);
        _sp = services.BuildServiceProvider();
        foreach (var hosted in _sp.GetServices<IHostedService>().Where(h => h is MarbotsBootstrapper or TriggerService or ChannelGateway))
            await hosted.StartAsync(default);
    }

    public async Task DisposeAsync()
    {
        Engine.Shutdown();
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static async Task<T> Eventually<T>(Func<Task<T?>> probe, int seconds = 20) where T : class
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (await probe() is { } v) return v;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met");
    }

    // ---------------------------------------------------------------- suggest mode

    private void ScriptBossDelegation()
    {
        Mock.Responder = req =>
        {
            var sys = req.Messages[0].Content ?? "";
            if (!sys.StartsWith("You are Boss Man", StringComparison.Ordinal)) return new ModelResponse { Content = "worker done" };
            if (req.Messages[^1].Role == "tool") return new ModelResponse { Content = "Boss: " + req.Messages[^1].Content };
            return new ModelResponse { ToolCalls = [new ToolCall { Id = "d", Name = "delegate_tasks", Arguments = """{"tasks":[{"key":"r","bot":"atlas","objective":"Research X"}]}""" }] };
        };
    }

    [Fact]
    public async Task Suggest_mode_waits_for_plan_approval()
    {
        var approvals = _sp.GetRequiredService<ApprovalService>();
        await approvals.SetDelegationModeAsync(DelegationMode.Suggest, "test");
        ScriptBossDelegation();
        var thread = await Engine.CreateThreadAsync(WellKnown.BossManId);
        var task = await Engine.SendAsync(thread.Id, "do X");
        var pending = await Eventually(async () => { var p = await approvals.PendingAsync(); return p.Count > 0 ? p[0] : null; });
        Assert.Equal("delegate_tasks", pending.ToolName);
        Assert.Contains("Proposed plan", pending.Summary);
        Assert.Contains("atlas", pending.Summary);
        Assert.DoesNotContain(await Engine.ListTasksAsync(), t => t.ParentTaskId == task.Id); // nothing started yet
        await approvals.ResolveAsync(pending.Id, true, ApprovalScope.Once, "test");
        task = await Engine.WaitAsync(task.Id, TimeSpan.FromSeconds(20));
        Assert.Contains("worker done", task.Result);
        Assert.Single(await Engine.ListTasksAsync(), t => t.ParentTaskId == task.Id);
    }

    [Fact]
    public async Task Suggest_mode_rejection_starts_nothing()
    {
        var approvals = _sp.GetRequiredService<ApprovalService>();
        await approvals.SetDelegationModeAsync(DelegationMode.Suggest, "test");
        ScriptBossDelegation();
        var thread = await Engine.CreateThreadAsync(WellKnown.BossManId);
        var task = await Engine.SendAsync(thread.Id, "do X");
        var pending = await Eventually(async () => { var p = await approvals.PendingAsync(); return p.Count > 0 ? p[0] : null; });
        await approvals.ResolveAsync(pending.Id, false, ApprovalScope.Once, "test");
        task = await Engine.WaitAsync(task.Id, TimeSpan.FromSeconds(20));
        Assert.Contains("did not approve this delegation plan", task.Result);
        Assert.DoesNotContain(await Engine.ListTasksAsync(), t => t.ParentTaskId == task.Id);
        Assert.Equal(DelegationMode.Suggest, (await approvals.GetSettingsAsync()).Delegation);
        await approvals.SetSkipApprovalsAsync(false, "test");
        Assert.Equal(DelegationMode.Suggest, (await approvals.GetSettingsAsync()).Delegation); // settings are preserved
    }

    // ---------------------------------------------------------------- triggers

    [Fact]
    public async Task Webhook_trigger_requires_secret_or_hmac()
    {
        var secrets = _sp.GetRequiredService<LocalSecretProvider>();
        secrets.Set("trigger:t1", "s3cret");
        var triggers = _sp.GetRequiredService<TriggerService>();
        var t = await triggers.SaveAsync(new TriggerConfig { Id = "t1", Name = "Issue", BotId = "atlas", SecretRef = "trigger:t1", PromptTemplate = "Triage: {{payload}}" });

        await Assert.ThrowsAsync<TriggerAuthException>(() => triggers.FireWebhookAsync(t.Id, "{}", "wrong", null, default));
        Mock.EnqueueText("triaged");
        var task = await triggers.FireWebhookAsync(t.Id, """{"title":"Crash"}""", "s3cret", null, default);
        Assert.Equal(TriggerService.ActorFor("t1"), task.AssignedBy);
        Assert.Contains("Crash", task.Objective);

        var body = """{"title":"Second"}""";
        var sig = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes(body)));
        Mock.EnqueueText("ok");
        Assert.NotNull(await triggers.FireWebhookAsync(t.Id, body, null, sig, default));
        await Assert.ThrowsAsync<ArgumentException>(() => triggers.SaveAsync(new TriggerConfig { Name = "no secret", PromptTemplate = "x" }));
    }

    [Fact]
    public async Task Event_trigger_chains_bots_without_looping()
    {
        var triggers = _sp.GetRequiredService<TriggerService>();
        await triggers.SaveAsync(new TriggerConfig
        {
            Name = "Summarise Atlas", Kind = TriggerKinds.Event, SourceBotId = "atlas", BotId = "wren",
            PromptTemplate = "Summarise for the newsletter: {{result}}",
        });
        await triggers.SaveAsync(new TriggerConfig
        {
            Name = "Echo Wren", Kind = TriggerKinds.Event, SourceBotId = "wren", BotId = "wren", PromptTemplate = "again: {{result}}",
        });
        Mock.Responder = req => new ModelResponse { Content = "out:" + req.Messages[^1].Content };
        var thread = await Engine.CreateThreadAsync("atlas");
        await Engine.WaitAsync((await Engine.SendAsync(thread.Id, "AI news")).Id, TimeSpan.FromSeconds(20));

        var wrenTask = await Eventually(async () => (await Engine.ListTasksAsync()).FirstOrDefault(t => t.BotId == "wren" && t.Objective.StartsWith("Summarise", StringComparison.Ordinal)));
        Assert.Contains("out:AI news", wrenTask.Objective);
        await Engine.WaitAsync(wrenTask.Id, TimeSpan.FromSeconds(20));
        // "Echo Wren" fires once for Wren's summary, but never for the task it started itself.
        await Task.Delay(1500);
        var echoes = (await Engine.ListTasksAsync()).Where(t => t.Objective.StartsWith("again:", StringComparison.Ordinal)).ToList();
        Assert.Single(echoes);
    }

    // ---------------------------------------------------------------- channels

    [Fact]
    public async Task Webhook_channel_round_trip_posts_reply_to_outbound_url()
    {
        var gateway = _sp.GetRequiredService<ChannelGateway>();
        var ch = await gateway.SaveAsync(new ChannelConfig
        {
            Name = "Zapier", Kind = ChannelKinds.Webhook, BotId = "wren",
            Settings = new() { ["outboundUrl"] = "https://hooks.example.test/out" },
        }, new Dictionary<string, string> { ["inboundSecret"] = "in", ["outboundSecret"] = "out" });
        Assert.Equal("channel:" + ch.Id + ":inboundSecret", ch.SecretRefs["inboundSecret"]);

        Mock.EnqueueText("Hello from Wren");
        var task = await gateway.ReceiveAsync(ch, new InboundMessage("conv-1", "u1", "Dina", "hi there"));
        Assert.Equal(ChannelGateway.ActorFor(ch.Id), task.AssignedBy);
        Assert.StartsWith("Dina: hi there", task.Objective);
        await Eventually(() => Task.FromResult(_http.Calls.IsEmpty ? null : "sent"));
        Assert.True(_http.Calls.TryPeek(out var call));
        var (method, url, body, _) = call;
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://hooks.example.test/out", url);
        Assert.Contains("Hello from Wren", body);
        Assert.Contains("conv-1", body);

        // same conversation → same thread (the bot remembers)
        Mock.EnqueueText("Again");
        var second = await gateway.ReceiveAsync(ch, new InboundMessage("conv-1", "u1", "Dina", "and now?"));
        Assert.Equal(task.ThreadId, second.ThreadId);
    }

    [Fact]
    public async Task Channel_allow_list_and_disabled_channels_are_enforced()
    {
        var gateway = _sp.GetRequiredService<ChannelGateway>();
        var ch = await gateway.SaveAsync(new ChannelConfig { Name = "Private", Kind = ChannelKinds.Webhook, AllowedSenders = ["boss"] });
        await Assert.ThrowsAsync<ChannelAuthException>(() => gateway.ReceiveAsync(ch, new InboundMessage("c", "stranger", null, "hi")));
        ch.Enabled = false;
        await Assert.ThrowsAsync<ChannelAuthException>(() => gateway.ReceiveAsync(ch, new InboundMessage("c", "boss", null, "hi")));
    }

    [Fact]
    public async Task Telegram_reply_uses_bot_api_and_long_messages_are_chunked()
    {
        var gateway = _sp.GetRequiredService<ChannelGateway>();
        var ch = await gateway.SaveAsync(new ChannelConfig { Name = "TG", Kind = ChannelKinds.Telegram, BotId = "wren", Settings = new() { ["mode"] = "webhook" } },
            new Dictionary<string, string> { ["token"] = "123:ABC" });
        Mock.EnqueueText(new string('x', 9000));
        using var update = JsonDocument.Parse("""{"update_id":1,"message":{"chat":{"id":42},"from":{"id":7,"first_name":"Budi"},"text":"halo"}}""");
        var msg = TelegramAdapter.Parse(update.RootElement)!;
        Assert.Equal(("42", "7", "Budi", "halo"), (msg.ConversationId, msg.SenderId, msg.SenderName, msg.Text));
        await gateway.ReceiveAsync(ch, msg);
        await Eventually(() => Task.FromResult(_http.Calls.Count >= 3 ? "ok" : null));
        Assert.All(_http.Calls, c => Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", c.Url));
        Assert.Contains("\"chat_id\":\"42\"", _http.Calls.First().Body);
    }

    [Fact]
    public async Task Slack_and_whatsapp_signatures_and_parsing()
    {
        var now = DateTimeOffset.UtcNow;
        var body = """{"type":"event_callback","event":{"type":"message","channel":"C1","user":"U1","text":"hi bot"}}""";
        var ts = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sig = "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("sign"), Encoding.UTF8.GetBytes($"v0:{ts}:{body}")));
        Assert.True(SlackAdapter.Verify("sign", ts, body, sig, now));
        Assert.False(SlackAdapter.Verify("sign", ts, body + " ", sig, now));
        Assert.False(SlackAdapter.Verify("sign", "1", body, sig, now)); // stale timestamp
        using (var doc = JsonDocument.Parse(body))
            Assert.Equal(("C1", "hi bot"), (SlackAdapter.Parse(doc.RootElement)!.ConversationId, SlackAdapter.Parse(doc.RootElement)!.Text));
        using (var botMsg = JsonDocument.Parse("""{"event":{"type":"message","channel":"C1","bot_id":"B","text":"echo"}}"""))
            Assert.Null(SlackAdapter.Parse(botMsg.RootElement));

        var wa = """{"entry":[{"changes":[{"value":{"contacts":[{"profile":{"name":"Sari"}}],"messages":[{"from":"6281","type":"text","text":{"body":"pesan kopi"}}]}}]}]}""";
        var waSig = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("app"), Encoding.UTF8.GetBytes(wa)));
        Assert.True(WhatsAppAdapter.VerifySignature("app", wa, waSig));
        Assert.False(WhatsAppAdapter.VerifySignature("app", wa, "sha256=00"));
        using var waDoc = JsonDocument.Parse(wa);
        var m = Assert.Single(WhatsAppAdapter.Parse(waDoc.RootElement));
        Assert.Equal(("6281", "Sari", "pesan kopi"), (m.ConversationId, m.SenderName, m.Text));
    }
}

/// <summary>Phase 3 over HTTP: web chat, inbound auth, Slack url_verification, triggers endpoint, delegation setting.</summary>
public sealed class IntegrationHttpTests : IClassFixture<IntegrationHttpTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mb-inthttp-" + Guid.NewGuid().ToString("N")[..8]);
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Marbots:DataDirectory", DataDir);
    }

    private readonly HttpClient _http;

    public IntegrationHttpTests(Fixture fx) => _http = fx.CreateClient();

    private async Task<JsonElement> CreateChannel(object channel, object? secrets = null)
    {
        var resp = await _http.PostAsJsonAsync("/api/v1/channels", new { channel, secrets });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("channel");
    }

    [Fact]
    public async Task Web_chat_page_and_conversation()
    {
        var ch = await CreateChannel(new { name = "Site", kind = "webchat", botId = "wren", settings = new { title = "Ask Wren" } });
        var id = ch.GetProperty("id").GetString();
        var page = await _http.GetStringAsync($"/webchat/{id}");
        Assert.Contains("Ask Wren", page);
        Assert.Contains("Gravicode", page);

        var cid = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync($"/webchat/{id}/messages", new { conversationId = "nope", text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsJsonAsync($"/webchat/{id}/messages", new { conversationId = cid, text = "hello" })).StatusCode);
        JsonElement messages = default;
        for (var i = 0; i < 100; i++)
        {
            var j = await _http.GetFromJsonAsync<JsonElement>($"/webchat/{id}/messages?conversationId={cid}");
            messages = j.GetProperty("messages");
            if (messages.EnumerateArray().Any(m => m.GetProperty("role").GetString() == "assistant")) break;
            await Task.Delay(100);
        }
        Assert.Contains(messages.EnumerateArray(), m => m.GetProperty("role").GetString() == "user" && m.GetProperty("text").GetString() == "hello");
        Assert.Contains(messages.EnumerateArray(), m => m.GetProperty("role").GetString() == "assistant");
        // another conversation sees nothing of this one
        var other = await _http.GetFromJsonAsync<JsonElement>($"/webchat/{id}/messages?conversationId={Guid.NewGuid()}");
        Assert.Empty(other.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task Inbound_requires_channel_secret()
    {
        var ch = await CreateChannel(new { name = "Hook", kind = "webhook" }, new { inboundSecret = "letmein" });
        var id = ch.GetProperty("id").GetString();
        var body = new { conversationId = "c1", text = "hi" };
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.PostAsJsonAsync($"/api/v1/channels/{id}/inbound", body)).StatusCode);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/channels/{id}/inbound") { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Marbots-Secret", "letmein");
        Assert.Equal(HttpStatusCode.Accepted, (await _http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Slack_url_verification_needs_valid_signature()
    {
        var ch = await CreateChannel(new { name = "Slack", kind = "slack" }, new { signingSecret = "sign", token = "xoxb-1" });
        var id = ch.GetProperty("id").GetString();
        var body = """{"type":"url_verification","challenge":"abc123"}""";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sig = "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("sign"), Encoding.UTF8.GetBytes($"v0:{ts}:{body}")));
        using var good = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/channels/{id}/slack") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        good.Headers.Add("X-Slack-Request-Timestamp", ts);
        good.Headers.Add("X-Slack-Signature", sig);
        var resp = await _http.SendAsync(good);
        Assert.Equal("abc123", await resp.Content.ReadAsStringAsync());
        using var bad = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/channels/{id}/slack") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        bad.Headers.Add("X-Slack-Request-Timestamp", ts);
        bad.Headers.Add("X-Slack-Signature", "v0=00");
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(bad)).StatusCode);
    }

    [Fact]
    public async Task A2a_message_stream_sends_updates_and_final_result()
    {
        var card = await _http.GetFromJsonAsync<JsonElement>("/a2a/wren/.well-known/agent-card.json");
        Assert.True(card.GetProperty("capabilities").GetProperty("streaming").GetBoolean());
        using var req = new HttpRequestMessage(HttpMethod.Post, "/a2a/wren")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0", id = 7, method = "message/stream",
                @params = new { message = new { role = "user", messageId = "m1", parts = new[] { new { kind = "text", text = "hello" } } } },
            }),
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        var kinds = new List<string>();
        var finalState = "";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var result = JsonDocument.Parse(line[6..]).RootElement.GetProperty("result");
            kinds.Add(result.GetProperty("kind").GetString()!);
            if (result.TryGetProperty("final", out var f) && f.GetBoolean()) finalState = result.GetProperty("status").GetProperty("state").GetString()!;
            if (kinds[^1] == "artifact-update") break;
        }
        Assert.Equal("task", kinds[0]);
        Assert.Contains("status-update", kinds);
        Assert.Equal("completed", finalState);
        Assert.Equal("artifact-update", kinds[^1]);
    }

    [Fact]
    public async Task Hook_endpoint_and_delegation_setting()
    {
        var resp = await _http.PostAsJsonAsync("/api/v1/triggers", new { trigger = new { name = "CI failed", kind = "webhook", botId = "quinn", promptTemplate = "Investigate: {{payload}}" }, secret = "hooksecret" });
        resp.EnsureSuccessStatusCode();
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsync($"/api/v1/hooks/{id}", new StringContent("{}"))).StatusCode);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/hooks/{id}") { Content = new StringContent("""{"job":"build"}""") };
        req.Headers.Add("X-Marbots-Secret", "hooksecret");
        var fired = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, fired.StatusCode);
        Assert.Contains("build", (await fired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("objective").GetString());

        Assert.Equal(HttpStatusCode.OK, (await _http.PutAsJsonAsync("/api/v1/system/delegation", new { mode = "Suggest" })).StatusCode);
        Assert.Equal("Suggest", (await _http.GetFromJsonAsync<JsonElement>("/api/v1/system/delegation")).GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PutAsJsonAsync("/api/v1/system/delegation", new { mode = "Chaos" })).StatusCode);
        await _http.PutAsJsonAsync("/api/v1/system/delegation", new { mode = "Auto" });
    }
}
