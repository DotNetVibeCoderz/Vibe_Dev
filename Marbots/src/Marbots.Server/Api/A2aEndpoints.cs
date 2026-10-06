using System.Text.Json;
using System.Text.Json.Nodes;
using Marbots.Abstractions;
using Marbots.Runtime;

namespace Marbots.Server.Api;

/// <summary>
/// Agent2Agent (A2A) interoperability (preview): every bot publishes an Agent Card and accepts JSON-RPC
/// <c>message/send</c>, <c>tasks/get</c> and <c>tasks/cancel</c>. A2A context ids map to Marbots threads.
/// </summary>
public static class A2aEndpoints
{
    public static void MapA2a(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/agent-card.json", (HttpContext ctx, BotRegistry r) => CardAsync(ctx, r, WellKnown.BossManId));
        app.MapGet("/a2a/{botId}/.well-known/agent-card.json", (string botId, HttpContext ctx, BotRegistry r) => CardAsync(ctx, r, botId));
        app.MapPost("/a2a/{botId}", HandleAsync).DisableAntiforgery();
    }

    private static async Task<IResult> CardAsync(HttpContext ctx, BotRegistry registry, string botId)
    {
        var bot = await registry.ResolveAsync(botId);
        if (bot is null) return Results.NotFound();
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var skills = new JsonArray();
        foreach (var s in bot.Skills.Where(s => s != "*"))
            skills.Add(new JsonObject { ["id"] = s, ["name"] = s, ["description"] = $"{bot.Name} can apply the '{s}' skill.", ["tags"] = new JsonArray(s) });
        if (skills.Count == 0)
            skills.Add(new JsonObject { ["id"] = "general", ["name"] = bot.Role, ["description"] = bot.Description, ["tags"] = new JsonArray("general") });
        var card = new JsonObject
        {
            ["protocolVersion"] = "0.3.0",
            ["name"] = $"{bot.Name} (Marbots)",
            ["description"] = $"{bot.Role}. {bot.Description}",
            ["url"] = $"{baseUrl}/a2a/{bot.Id}",
            ["preferredTransport"] = "JSONRPC",
            ["version"] = "0.1.0",
            ["provider"] = new JsonObject { ["organization"] = "Gravicode Studios", ["url"] = "https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/Marbots" },
            ["capabilities"] = new JsonObject { ["streaming"] = true, ["pushNotifications"] = false, ["stateTransitionHistory"] = false },
            ["defaultInputModes"] = new JsonArray("text/plain"),
            ["defaultOutputModes"] = new JsonArray("text/plain", "text/markdown"),
            ["skills"] = skills,
        };
        return Results.Content(card.ToJsonString(), "application/json");
    }

    private static async Task<IResult> HandleAsync(string botId, HttpRequest req, BotRegistry registry, MarbotsEngine engine, IMessageStore messages, IEventBus bus)
    {
        JsonNode? body;
        try { body = await JsonNode.ParseAsync(req.Body); }
        catch (JsonException) { return Rpc(null, error: (-32700, "Parse error")); }
        var id = body?["id"]?.DeepClone();
        var method = body?["method"]?.GetValue<string>();
        var p = body?["params"];
        var bot = await registry.ResolveAsync(botId);
        if (bot is null) return Rpc(id, error: (-32001, "Agent not found"));

        switch (method)
        {
            case "message/stream":
            {
                var msg = p?["message"];
                var text = string.Join("\n", (msg?["parts"] as JsonArray ?? []).Where(x => x?["kind"]?.GetValue<string>() is "text" or null && x?["text"] is not null).Select(x => x!["text"]!.GetValue<string>()));
                if (text.Length == 0) return Rpc(id, error: (-32602, "message.parts must contain text"));
                var contextId = msg?["contextId"]?.GetValue<string>();
                var thread = contextId is null ? null : await engine.GetThreadAsync(contextId);
                if (thread is null || thread.BotId != bot.Id) thread = await engine.CreateThreadAsync(bot.Id, "A2A: " + AgentRuntime.Preview(text, 40));
                return new A2aStreamResult(id, thread.Id, text, engine, messages, bus);
            }
            case "message/send":
            {
                var msg = p?["message"];
                var text = string.Join("\n", (msg?["parts"] as JsonArray ?? []).Where(x => x?["kind"]?.GetValue<string>() is "text" or null && x?["text"] is not null).Select(x => x!["text"]!.GetValue<string>()));
                if (text.Length == 0) return Rpc(id, error: (-32602, "message.parts must contain text"));
                var contextId = msg?["contextId"]?.GetValue<string>();
                var thread = contextId is null ? null : await engine.GetThreadAsync(contextId);
                if (thread is null || thread.BotId != bot.Id) thread = await engine.CreateThreadAsync(bot.Id, "A2A: " + AgentRuntime.Preview(text, 40));
                var task = await engine.SendAsync(thread.Id, text);
                var blocking = p?["configuration"]?["blocking"]?.GetValue<bool>() ?? true;
                if (blocking) task = await engine.WaitAsync(task.Id, TimeSpan.FromMinutes(10));
                return Rpc(id, await TaskJsonAsync(task, messages));
            }
            case "tasks/get":
            {
                var task = await engine.GetTaskAsync(p?["id"]?.GetValue<string>() ?? "");
                return task is null ? Rpc(id, error: (-32001, "Task not found")) : Rpc(id, await TaskJsonAsync(task, messages));
            }
            case "tasks/cancel":
            {
                var taskId = p?["id"]?.GetValue<string>() ?? "";
                await engine.CancelAsync(taskId);
                var task = await engine.WaitAsync(taskId, TimeSpan.FromSeconds(10));
                return Rpc(id, await TaskJsonAsync(task, messages));
            }
            default:
                return Rpc(id, error: (-32601, $"Method '{method}' not supported"));
        }
    }

    internal static async Task<JsonObject> TaskJsonAsync(TaskRecord task, IMessageStore messages)
    {
        var state = task.State switch
        {
            TaskState.Completed => "completed",
            TaskState.Failed or TaskState.TimedOut => "failed",
            TaskState.Cancelled => "canceled",
            TaskState.WaitingForHuman => "input-required",
            TaskState.Queued => "submitted",
            _ => "working",
        };
        var text = task.Result ?? task.Error;
        if (text is null && task.State.IsTerminal())
            text = (await messages.ListAsync(task.TranscriptId, 0, 5000)).LastOrDefault(m => m.TaskId == task.Id && m.Role == "assistant")?.Content;
        var status = new JsonObject { ["state"] = state, ["timestamp"] = (task.CompletedAt ?? DateTimeOffset.UtcNow).ToString("O") };
        if (text is not null)
            status["message"] = new JsonObject
            {
                ["kind"] = "message", ["role"] = "agent", ["messageId"] = Ids.New("msg"), ["taskId"] = task.Id, ["contextId"] = task.ThreadId,
                ["parts"] = new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = text }),
            };
        var result = new JsonObject { ["kind"] = "task", ["id"] = task.Id, ["contextId"] = task.ThreadId, ["status"] = status };
        if (task.State == TaskState.Completed && text is not null)
            result["artifacts"] = new JsonArray(new JsonObject
            {
                ["artifactId"] = task.Id + "-response", ["name"] = "response",
                ["parts"] = new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = text }),
            });
        return result;
    }

    private static IResult Rpc(JsonNode? id, JsonNode? result = null, (int Code, string Message)? error = null)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error is { } e) o["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
        else o["result"] = result;
        return Results.Content(o.ToJsonString(), "application/json");
    }
}

/// <summary>
/// A2A <c>message/stream</c>: Server-Sent Events carrying JSON-RPC responses — the task, status updates while the bot
/// works (thinking, tools, delegation), then the final task with its artifact.
/// </summary>
internal sealed class A2aStreamResult(JsonNode? id, string threadId, string text, MarbotsEngine engine, IMessageStore messages, IEventBus bus) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        var ct = http.RequestAborted;
        // Subscribe before sending so no update is missed.
        var stream = bus.StreamAsync(e => e.ThreadId == threadId, ct).GetAsyncEnumerator(ct);
        var task = await engine.SendAsync(threadId, text, ct);
        await WriteAsync(http, await A2aEndpoints.TaskJsonAsync(task, messages), ct);
        try
        {
            while (await stream.MoveNextAsync())
            {
                var e = stream.Current;
                var state = e.Type switch
                {
                    EventTypes.ApprovalRequested => "input-required",
                    EventTypes.TaskStateChanged when e.TaskId == task.Id && e.Data is "Completed" or "Failed" or "Cancelled" or "TimedOut" => null,
                    EventTypes.AgentThinkingStarted or EventTypes.ToolCallStarted or EventTypes.TaskDelegated or EventTypes.ToolCallCompleted => "working",
                    _ => "",
                };
                if (state is null)
                {
                    var done = await engine.GetTaskAsync(task.Id, ct) ?? task;
                    var final = await A2aEndpoints.TaskJsonAsync(done, messages);
                    await WriteAsync(http, new JsonObject
                    {
                        ["kind"] = "status-update", ["taskId"] = task.Id, ["contextId"] = threadId,
                        ["status"] = final["status"]!.DeepClone(), ["final"] = true,
                    }, ct);
                    if (final["artifacts"] is JsonArray arts)
                        foreach (var a in arts)
                            await WriteAsync(http, new JsonObject { ["kind"] = "artifact-update", ["taskId"] = task.Id, ["contextId"] = threadId, ["artifact"] = a!.DeepClone(), ["lastChunk"] = true }, ct);
                    return;
                }
                if (state.Length == 0) continue;
                await WriteAsync(http, new JsonObject
                {
                    ["kind"] = "status-update", ["taskId"] = task.Id, ["contextId"] = threadId, ["final"] = false,
                    ["status"] = new JsonObject
                    {
                        ["state"] = state, ["timestamp"] = e.Timestamp.ToString("O"),
                        ["message"] = new JsonObject
                        {
                            ["kind"] = "message", ["role"] = "agent", ["messageId"] = Ids.New("msg"), ["taskId"] = task.Id, ["contextId"] = threadId,
                            ["parts"] = new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = $"{e.BotId}: {e.Type} {e.Message}".Trim() }),
                        },
                    },
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { await stream.DisposeAsync(); }
    }

    private async Task WriteAsync(HttpContext http, JsonObject result, CancellationToken ct)
    {
        var envelope = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
        await http.Response.WriteAsync($"data: {envelope.ToJsonString()}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
