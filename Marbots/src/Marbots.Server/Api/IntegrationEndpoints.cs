using System.Text;
using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Runtime;

namespace Marbots.Server.Api;

public sealed record SaveChannelRequest(ChannelConfig Channel, Dictionary<string, string>? Secrets);
public sealed record ChannelView(ChannelConfig Channel, List<string> ConfiguredSecrets, string InboundUrl);
public sealed record InboundRequest(string ConversationId, string Text, string? SenderId, string? SenderName);
public sealed record SaveTriggerRequest(TriggerConfig Trigger, string? Secret);
public sealed record DelegationSettings(string Mode);
public sealed record WebChatPost(string ConversationId, string Text, string? Name);
public sealed record WebChatMessage(long Seq, string Role, string Text, DateTimeOffset At);

/// <summary>Phase 3 endpoints: channels (incl. provider webhooks and the public web chat), triggers and delegation mode.</summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrations(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Integrations");
        api.AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            catch (ChannelAuthException ex) { return Results.Problem(ex.Message, statusCode: 403); }
            catch (TriggerAuthException ex) { return Results.Problem(ex.Message, statusCode: 401); }
            catch (BotValidationException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });

        // ---------- delegation mode ----------
        api.MapGet("/system/delegation", async (ApprovalService a) => new DelegationSettings((await a.GetSettingsAsync()).Delegation.ToString()));
        api.MapPut("/system/delegation", async (DelegationSettings req, ApprovalService a) =>
        {
            if (!Enum.TryParse<DelegationMode>(req.Mode, true, out var mode) || mode == DelegationMode.Manual)
                return Results.Problem("Mode must be Auto or Suggest.", statusCode: 400);
            await a.SetDelegationModeAsync(mode, "api");
            return Results.Ok(new DelegationSettings(mode.ToString()));
        });

        // ---------- channels ----------
        api.MapGet("/channels", async (ChannelGateway g, HttpContext ctx) => (await g.ListAsync()).Select(c => View(c, ctx)));
        api.MapGet("/channels/{id}", async (string id, ChannelGateway g, HttpContext ctx) => await g.GetAsync(id) is { } c ? Results.Ok(View(c, ctx)) : Results.NotFound());
        api.MapPost("/channels", async (SaveChannelRequest req, ChannelGateway g, HttpContext ctx) => View(await g.SaveAsync(req.Channel, req.Secrets), ctx));
        api.MapDelete("/channels/{id}", async (string id, ChannelGateway g) => await g.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/channels/{id}/test", async (string id, InboundRequest req, ChannelGateway g) =>
        {
            var c = await g.GetAsync(id);
            if (c is null) return Results.NotFound();
            await g.SendAsync(c, req.ConversationId, req.Text);
            return Results.NoContent();
        });

        // Generic inbound (webhook channels, relays, Discord bots, Teams workflows). Auth: X-Marbots-Secret = inboundSecret.
        api.MapPost("/channels/{id}/inbound", async (string id, InboundRequest req, HttpRequest http, ChannelGateway g) =>
        {
            var c = await g.GetAsync(id);
            if (c is null) return Results.NotFound();
            var expected = g.Secret(c, "inboundSecret");
            if (string.IsNullOrEmpty(expected) || !TriggerService.Verify(expected, "", http.Headers["X-Marbots-Secret"], null))
                throw new ChannelAuthException("Missing or invalid X-Marbots-Secret.");
            var task = await g.ReceiveAsync(c, new InboundMessage(req.ConversationId, req.SenderId ?? req.ConversationId, req.SenderName, req.Text));
            return Results.Accepted($"/api/v1/tasks/{task.Id}", task);
        }).DisableAntiforgery();

        // Telegram webhook mode (optional; polling is the default). Auth: X-Telegram-Bot-Api-Secret-Token = webhookSecret.
        api.MapPost("/channels/{id}/telegram", async (string id, HttpRequest http, ChannelGateway g) =>
        {
            var c = await g.GetAsync(id);
            if (c is null || c.Kind != ChannelKinds.Telegram) return Results.NotFound();
            var expected = g.Secret(c, "webhookSecret");
            if (string.IsNullOrEmpty(expected) || !TriggerService.Verify(expected, "", http.Headers["X-Telegram-Bot-Api-Secret-Token"], null))
                throw new ChannelAuthException("Invalid Telegram secret token.");
            using var doc = await JsonDocument.ParseAsync(http.Body);
            if (TelegramAdapter.Parse(doc.RootElement) is { } msg) await g.ReceiveAsync(c, msg);
            return Results.Ok();
        }).DisableAntiforgery();

        // Slack Events API. Auth: signing secret (X-Slack-Signature / X-Slack-Request-Timestamp).
        api.MapPost("/channels/{id}/slack", async (string id, HttpRequest http, ChannelGateway g) =>
        {
            var c = await g.GetAsync(id);
            if (c is null || c.Kind != ChannelKinds.Slack) return Results.NotFound();
            var body = await new StreamReader(http.Body, Encoding.UTF8).ReadToEndAsync();
            var secret = g.Secret(c, "signingSecret");
            if (string.IsNullOrEmpty(secret) || !SlackAdapter.Verify(secret, http.Headers["X-Slack-Request-Timestamp"].ToString(), body,
                    http.Headers["X-Slack-Signature"].ToString(), DateTimeOffset.UtcNow))
                throw new ChannelAuthException("Invalid Slack signature.");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "url_verification")
                return Results.Text(doc.RootElement.GetProperty("challenge").GetString() ?? "", "text/plain");
            if (http.Headers.ContainsKey("X-Slack-Retry-Num")) return Results.Ok(); // Slack retries: already handled
            if (SlackAdapter.Parse(doc.RootElement) is { } msg) _ = g.ReceiveAsync(c, msg);
            return Results.Ok();
        }).DisableAntiforgery();

        // WhatsApp Cloud API: GET verification + POST messages (X-Hub-Signature-256 with the app secret).
        api.MapGet("/channels/{id}/whatsapp", async (string id, HttpRequest http, ChannelGateway g) =>
        {
            var c = await g.GetAsync(id);
            if (c is null || c.Kind != ChannelKinds.WhatsApp) return Results.NotFound();
            var verify = g.Secret(c, "verifyToken");
            return http.Query["hub.mode"] == "subscribe" && !string.IsNullOrEmpty(verify) && http.Query["hub.verify_token"] == verify
                ? Results.Text(http.Query["hub.challenge"].ToString(), "text/plain")
                : Results.StatusCode(403);
        });
        api.MapPost("/channels/{id}/whatsapp", async (string id, HttpRequest http, ChannelGateway g) =>
        {
            var c = await g.GetAsync(id);
            if (c is null || c.Kind != ChannelKinds.WhatsApp) return Results.NotFound();
            var body = await new StreamReader(http.Body, Encoding.UTF8).ReadToEndAsync();
            var appSecret = g.Secret(c, "appSecret");
            if (string.IsNullOrEmpty(appSecret) || !WhatsAppAdapter.VerifySignature(appSecret, body, http.Headers["X-Hub-Signature-256"]))
                throw new ChannelAuthException("Invalid WhatsApp signature.");
            using var doc = JsonDocument.Parse(body);
            foreach (var msg in WhatsAppAdapter.Parse(doc.RootElement).ToList()) _ = g.ReceiveAsync(c, msg);
            return Results.Ok();
        }).DisableAntiforgery();

        // ---------- triggers ----------
        api.MapGet("/triggers", (TriggerService t) => t.ListAsync());
        api.MapPost("/triggers", async (SaveTriggerRequest req, TriggerService t, LocalSecretProvider secrets) =>
        {
            var trigger = req.Trigger;
            if (string.IsNullOrEmpty(trigger.Id)) trigger.Id = Ids.New("trg");
            if (!string.IsNullOrEmpty(req.Secret))
            {
                trigger.SecretRef = $"trigger:{trigger.Id}";
                secrets.Set(trigger.SecretRef, req.Secret);
            }
            return await t.SaveAsync(trigger);
        });
        api.MapDelete("/triggers/{id}", async (string id, TriggerService t) => await t.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/hooks/{id}", async (string id, HttpRequest http, TriggerService t) =>
        {
            var body = await new StreamReader(http.Body, Encoding.UTF8).ReadToEndAsync();
            var task = await t.FireWebhookAsync(id, body, http.Headers["X-Marbots-Secret"], http.Headers["X-Marbots-Signature"], http.HttpContext.RequestAborted);
            return Results.Accepted($"/api/v1/tasks/{task.Id}", task);
        }).DisableAntiforgery();

        // ---------- public web chat (no API key; scoped to one conversation) ----------
        app.MapGet("/webchat/{id}", async (string id, ChannelGateway g) =>
            await g.GetAsync(id) is { Kind: ChannelKinds.WebChat, Enabled: true } c ? Results.Content(WebChatPage.Html(c), "text/html") : Results.NotFound());
        app.MapPost("/webchat/{id}/messages", async (string id, WebChatPost req, ChannelGateway g) =>
        {
            if (await g.GetAsync(id) is not { Kind: ChannelKinds.WebChat, Enabled: true } c) return Results.NotFound();
            if (!Guid.TryParse(req.ConversationId, out _)) return Results.Problem("conversationId must be a GUID.", statusCode: 400);
            var task = await g.ReceiveAsync(c, new InboundMessage(req.ConversationId, req.ConversationId, string.IsNullOrWhiteSpace(req.Name) ? null : req.Name, req.Text));
            return Results.Accepted(value: new { task.Id });
        }).DisableAntiforgery();
        app.MapGet("/webchat/{id}/messages", async (string id, string conversationId, long? after, ChannelGateway g, IMessageStore messages, IDocumentStore<TaskRecord> tasks) =>
        {
            if (await g.GetAsync(id) is not { Kind: ChannelKinds.WebChat, Enabled: true }) return Results.NotFound();
            var threadId = await g.ThreadForAsync(id, conversationId);
            if (threadId is null) return Results.Ok(new { messages = Array.Empty<WebChatMessage>(), working = false });
            var list = (await messages.ListAsync(threadId, after ?? 0, 200))
                .Where(m => m.Role is "user" or "assistant" && m.ToolCalls is null && m.Content.Length > 0)
                .Select(m => new WebChatMessage(m.Seq, m.Role, m.Content, m.CreatedAt)).ToList();
            var working = (await tasks.ListAsync()).Any(t => t.ThreadId == threadId && !t.State.IsTerminal());
            return Results.Ok(new { messages = list, working });
        });
    }

    private static ChannelView View(ChannelConfig c, HttpContext ctx)
    {
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var inbound = c.Kind switch
        {
            ChannelKinds.WebChat => $"{baseUrl}/webchat/{c.Id}",
            ChannelKinds.Slack => $"{baseUrl}/api/v1/channels/{c.Id}/slack",
            ChannelKinds.WhatsApp => $"{baseUrl}/api/v1/channels/{c.Id}/whatsapp",
            ChannelKinds.Telegram => ChannelContext.Setting(c, "mode", "polling") == "webhook" ? $"{baseUrl}/api/v1/channels/{c.Id}/telegram" : "(long polling — no public URL needed)",
            _ => $"{baseUrl}/api/v1/channels/{c.Id}/inbound",
        };
        return new ChannelView(c, [.. c.SecretRefs.Keys], inbound);
    }
}
