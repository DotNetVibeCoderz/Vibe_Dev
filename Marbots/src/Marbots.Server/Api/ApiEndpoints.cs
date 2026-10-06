using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Runtime;
using Marbots.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Marbots.Server.Api;

public sealed record SendMessageRequest(string Text, bool Wait = false, int TimeoutSeconds = 300);
public sealed record SendMessageResponse(TaskRecord Task, ChatMessage? Reply);
public sealed record CreateThreadRequest(string BotId, string? Title);
public sealed record UpdateThreadRequest(string? Title, bool? Pinned, bool? Archived);
public sealed record ResetThreadRequest(bool ForgetMemory = false);
public sealed record ForkThreadRequest(long UptoSeq = 0);
public sealed record ResolveApprovalRequest(string Scope = "Once");
public sealed record InstallSkillRequest(string Source);
public sealed record CreateSkillRequest(string Name, string Description, string Body);
public sealed record FromTemplateRequest(string? Name);
public sealed record WorkspaceFile(string Path, long Size, DateTimeOffset Modified);
public sealed record SystemInfo(string Product, string Version, string Credits, string CreditsEn, bool ModelConfigured, IReadOnlyList<string> Profiles, string DataDirectory);
public sealed record ProviderRequest(string Name, string Kind, string Endpoint, string? ApiKey, string? Model, List<string>? Models = null);
public sealed record ModelProfileInfo(string Name, string Provider, string Model, List<string> Fallbacks);
public sealed record ModelCatalog(string Default, List<string> Choices, List<ModelProfileInfo> Profiles);
public sealed record SetModelRequest(string Model);
public sealed record ApprovalSettings(bool DangerouslySkipApprovals);
public sealed record BotModelInfo(string BotId, string Setting, string Effective, bool UsesDefault, string? Warning);

public static class ApiEndpoints
{
    public static void MapMarbotsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Marbots");
        api.AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (BotValidationException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });

        // ---------- system ----------
        api.MapGet("/system", (IModelRouter router, MarbotsOptions o) => new SystemInfo(
            "Marbots", typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            WellKnown.Credits, WellKnown.CreditsEn, router.IsConfigured, router.Profiles.Select(p => $"{p.Name} → {p.Provider}/{p.Model}").ToList(), o.DataDirectory));

        api.MapPost("/system/providers", async (ProviderRequest req, IDocumentStore<ProviderConfig> providers, IDocumentStore<ModelProfile> profiles, LocalSecretProvider secrets, ModelRouter router) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Endpoint)) return Results.Problem("Name and endpoint are required.", statusCode: 400);
            var models = req.Models ?? [];
            if (!string.IsNullOrWhiteSpace(req.Model) && !models.Contains(req.Model)) models.Insert(0, req.Model);
            await providers.UpsertAsync(new ProviderConfig { Name = req.Name, Kind = req.Kind, Endpoint = req.Endpoint, Models = models });
            if (!string.IsNullOrEmpty(req.ApiKey)) secrets.Set($"provider:{req.Name}:apikey", req.ApiKey);
            await router.ReloadAsync();
            if (!string.IsNullOrWhiteSpace(req.Model) && router.IsConfigured) await router.SetDefaultAsync($"{req.Name}/{req.Model}");
            return Results.Ok(new { router.IsConfigured });
        });

        api.MapGet("/system/approvals", (ApprovalService a) => new ApprovalSettings(a.SkipApprovals));
        api.MapPut("/system/approvals", async (ApprovalSettings req, ApprovalService a) =>
        {
            await a.SetSkipApprovalsAsync(req.DangerouslySkipApprovals, "api");
            return new ApprovalSettings(a.SkipApprovals);
        });

        // ---------- models ----------
        api.MapGet("/models", (ModelRouter r) => new ModelCatalog(
            ModelRouter.Describe(r.Default), [.. r.Choices],
            r.Profiles.Select(p => new ModelProfileInfo(p.Name, p.Provider, p.Model, p.Fallbacks)).ToList()));
        api.MapPut("/models/default", async (SetModelRequest req, ModelRouter r) =>
        {
            await r.SetDefaultAsync(req.Model);
            return Results.Ok(new { Default = ModelRouter.Describe(r.Default) });
        });

        // ---------- bots ----------
        api.MapGet("/bots", (BotRegistry r) => r.ActiveAsync());
        api.MapGet("/bots/{id}", async (string id, BotRegistry r) => await r.ResolveAsync(id) is { } b ? Results.Ok(b) : Results.NotFound());
        api.MapPost("/bots", async (BotDefinition bot, BotRegistry r) => Results.Created($"/api/v1/bots/{bot.Id}", await r.CreateAsync(bot)));
        api.MapPut("/bots/{id}", async (string id, BotDefinition bot, BotRegistry r) => { bot.Id = id; return await r.UpdateAsync(bot); });
        api.MapDelete("/bots/{id}", async (string id, BotRegistry r) => await r.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());
        api.MapGet("/bots/{id}/model", async (string id, BotRegistry r, ModelRouter router) =>
        {
            var bot = await r.ResolveAsync(id);
            if (bot is null) return Results.NotFound();
            var res = router.Resolve(bot.ModelProfile);
            return Results.Ok(new BotModelInfo(bot.Id, bot.ModelProfile, res.Label, ModelRouter.IsDefaultSetting(bot.ModelProfile) || res.Fallback, res.Warning));
        });
        api.MapPut("/bots/{id}/model", async (string id, SetModelRequest req, BotRegistry r, ModelRouter router) =>
        {
            var bot = await r.ResolveAsync(id);
            if (bot is null) return Results.NotFound();
            var res = router.Resolve(req.Model);
            if (res.Fallback && !ModelRouter.IsDefaultSetting(req.Model)) return Results.Problem(res.Warning, statusCode: 400);
            bot.ModelProfile = string.IsNullOrWhiteSpace(req.Model) ? ModelRouter.DefaultProfile : req.Model.Trim();
            await r.UpdateAsync(bot);
            return Results.Ok(new BotModelInfo(bot.Id, bot.ModelProfile, res.Label, ModelRouter.IsDefaultSetting(bot.ModelProfile), null));
        });
        api.MapPost("/bots/{id}/pause", async (string id, BotRegistry r) => { await r.SetStatusAsync(id, BotStatus.Paused); return Results.NoContent(); });
        api.MapPost("/bots/{id}/resume", async (string id, BotRegistry r) => { await r.SetStatusAsync(id, BotStatus.Ready); return Results.NoContent(); });
        api.MapPost("/bots/from-template/{templateId}", async (string templateId, FromTemplateRequest? req, TemplateService t, BotRegistry r) =>
        {
            var tpl = await t.GetAsync(templateId);
            return tpl is null ? Results.NotFound() : Results.Ok(await r.CreateAsync(BotRegistry.FromTemplate(tpl, req?.Name)));
        });
        api.MapGet("/bots/{id}/export", async (string id, bool? includeMemory, BotPackageService p, BotRegistry r) =>
        {
            var bot = await r.ResolveAsync(id);
            if (bot is null) return Results.NotFound();
            var bytes = await p.ExportAsync(bot.Id, includeMemory ?? false, default);
            return Results.File(bytes, "application/zip", $"{bot.Id}.marbot");
        });
        api.MapPost("/bots/import", async (HttpRequest req, BotPackageService p) =>
        {
            using var ms = new MemoryStream();
            if (req.HasFormContentType && req.Form.Files.Count > 0) await req.Form.Files[0].CopyToAsync(ms);
            else await req.Body.CopyToAsync(ms);
            ms.Position = 0;
            return Results.Ok(await p.ImportAsync(ms, default));
        }).DisableAntiforgery();

        // ---------- templates ----------
        api.MapGet("/templates", (string? q, string? category, TemplateService t) => t.ListAsync(q, category));
        api.MapGet("/templates/{id}", async (string id, TemplateService t) => await t.GetAsync(id) is { } x ? Results.Ok(x) : Results.NotFound());
        api.MapPost("/templates", (BotTemplate tpl, TemplateService t) => t.SaveAsync(tpl));
        api.MapPut("/templates/{id}", (string id, BotTemplate tpl, TemplateService t) => { tpl.Id = id; return t.SaveAsync(tpl); });
        api.MapDelete("/templates/{id}", async (string id, TemplateService t) => await t.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        // ---------- threads & chat ----------
        api.MapGet("/threads", (string? botId, MarbotsEngine e) => e.ListThreadsAsync(botId));
        api.MapPost("/threads", (CreateThreadRequest req, MarbotsEngine e) => e.CreateThreadAsync(req.BotId, req.Title));
        api.MapGet("/threads/{id}", async (string id, MarbotsEngine e) => await e.GetThreadAsync(id) is { } t ? Results.Ok(t) : Results.NotFound());
        api.MapPatch("/threads/{id}", (string id, UpdateThreadRequest req, MarbotsEngine e) => e.UpdateThreadAsync(id, t =>
        {
            if (req.Title is { Length: > 0 }) t.Title = req.Title;
            if (req.Pinned is { } p) t.Pinned = p;
            if (req.Archived is { } a) t.Archived = a;
        }));
        api.MapDelete("/threads/{id}", async (string id, MarbotsEngine e) => { await e.DeleteThreadAsync(id); return Results.NoContent(); });
        api.MapGet("/threads/{id}/messages", (string id, long? after, IMessageStore m) => m.ListAsync(id, after ?? 0, 1000));
        api.MapPost("/threads/{id}/messages", async (string id, SendMessageRequest req, MarbotsEngine e, IMessageStore m, CancellationToken ct) =>
        {
            var task = await e.SendAsync(id, req.Text, ct);
            if (!req.Wait) return Results.Accepted($"/api/v1/tasks/{task.Id}", new SendMessageResponse(task, null));
            task = await e.WaitAsync(task.Id, TimeSpan.FromSeconds(Math.Clamp(req.TimeoutSeconds, 1, 3600)), ct);
            var reply = (await m.ListAsync(id, 0, 5000, ct)).LastOrDefault(x => x.TaskId == task.Id && x.Role is "assistant" or "system" && x.ToolCalls is null);
            return Results.Ok(new SendMessageResponse(task, reply));
        });
        api.MapPost("/threads/{id}/reset", (string id, ResetThreadRequest req, MarbotsEngine e, IMemoryStore mem) => e.ResetContextAsync(id, req.ForgetMemory, mem));
        api.MapPost("/threads/{id}/fork", (string id, ForkThreadRequest req, MarbotsEngine e) => e.ForkAsync(id, req.UptoSeq));
        api.MapGet("/threads/{id}/export", async (string id, MarbotsEngine e) => Results.Text(await e.ExportTranscriptAsync(id), "text/markdown"));
        api.MapGet("/threads/{id}/events", (string id, HttpContext ctx, IEventBus bus, IEventStore store, long? after) =>
            Sse.StreamAsync(ctx, bus, store, e => e.ThreadId == id || e.ThreadId == null && e.Type == EventTypes.ApprovalRequested, id, after));
        api.MapGet("/threads/{id}/files", (string id, MarbotsEngine e) => WorkspaceFiles.List(e.WorkspaceFor(id)));
        api.MapGet("/threads/{id}/files/{**path}", (string id, string path, MarbotsEngine e) => WorkspaceFiles.Download(e.WorkspaceFor(id), path));

        // ---------- tasks ----------
        api.MapGet("/tasks", async (string? threadId, string? state, MarbotsEngine e) =>
            (await e.ListTasksAsync()).Where(t => (threadId is null || t.ThreadId == threadId) && (state is null || t.State.ToString().Equals(state, StringComparison.OrdinalIgnoreCase))).Take(500));
        api.MapGet("/tasks/{id}", async (string id, MarbotsEngine e) => await e.GetTaskAsync(id) is { } t ? Results.Ok(t) : Results.NotFound());
        api.MapGet("/tasks/{id}/transcript", async (string id, MarbotsEngine e, IMessageStore m) =>
            await e.GetTaskAsync(id) is { } t ? Results.Ok((await m.ListAsync(t.TranscriptId, 0, 5000)).Where(x => t.Depth > 0 || x.TaskId == t.Id)) : Results.NotFound());
        api.MapPost("/tasks/{id}/cancel", async (string id, MarbotsEngine e) => await e.CancelAsync(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/tasks/{id}/retry", (string id, MarbotsEngine e) => e.RetryAsync(id));

        // ---------- approvals ----------
        api.MapGet("/approvals", async (string? state, ApprovalService a) =>
            state is "pending" or null ? await a.PendingAsync() : await a.AllAsync());
        api.MapPost("/approvals/{id}/approve", async (string id, ResolveApprovalRequest? req, ApprovalService a) =>
            await a.ResolveAsync(id, true, Enum.TryParse<ApprovalScope>(req?.Scope, true, out var s) ? s : ApprovalScope.Once, "api") is { } r ? Results.Ok(r) : Results.NotFound());
        api.MapPost("/approvals/{id}/reject", async (string id, ApprovalService a) =>
            await a.ResolveAsync(id, false, ApprovalScope.Once, "api") is { } r ? Results.Ok(r) : Results.NotFound());

        // ---------- skills ----------
        api.MapGet("/skills", (SkillRegistry s) => s.All);
        api.MapPost("/skills/install", async (InstallSkillRequest req, SkillRegistry s, CancellationToken ct) => await s.InstallAsync(req.Source, ct));
        api.MapPost("/skills", async (CreateSkillRequest req, SkillRegistry s, CancellationToken ct) => await s.CreateAsync(req.Name, req.Description, req.Body, false, ct));
        api.MapDelete("/skills/{name}", (string name, SkillRegistry s) => s.Uninstall(name) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/skills/{name}/approve", (string name, SkillRegistry s) => s.ApprovePending(name) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/skills/{name}/reject", (string name, SkillRegistry s) => s.RejectPending(name) ? Results.NoContent() : Results.NotFound());

        // ---------- MCP ----------
        api.MapGet("/mcp", (IDocumentStore<McpServerConfig> s) => s.ListAsync());
        api.MapGet("/mcp/status", (McpManager m) => m.Status());
        api.MapPost("/mcp", async (McpServerConfig cfg, IDocumentStore<McpServerConfig> s) =>
        {
            if (string.IsNullOrWhiteSpace(cfg.Id)) cfg.Id = Ids.Slug(cfg.Name);
            cfg.IsCatalogEntry = false;
            await s.UpsertAsync(cfg);
            return cfg;
        });
        api.MapPost("/mcp/{id}/install", async (string id, IDocumentStore<McpServerConfig> s) =>
        {
            var c = await s.GetAsync(id);
            if (c is null) return Results.NotFound();
            c.IsCatalogEntry = false;
            c.Enabled = true;
            await s.UpsertAsync(c);
            return Results.Ok(c);
        });
        api.MapPost("/mcp/{id}/uninstall", async (string id, IDocumentStore<McpServerConfig> s, McpManager m) =>
        {
            var c = await s.GetAsync(id);
            if (c is null) return Results.NotFound();
            c.IsCatalogEntry = true;
            await s.UpsertAsync(c);
            await m.RestartAsync(id);
            return Results.Ok(c);
        });
        api.MapDelete("/mcp/{id}", async (string id, IDocumentStore<McpServerConfig> s, McpManager m) =>
        {
            await m.RestartAsync(id);
            return await s.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();
        });
        api.MapPost("/mcp/{id}/test", async (string id, IDocumentStore<McpServerConfig> s, McpManager m, MarbotsOptions o, CancellationToken ct) =>
        {
            var c = await s.GetAsync(id);
            if (c is null) return Results.NotFound();
            try
            {
                return Results.Ok(await m.GetToolsAsync(c, o.DataPath("workspaces", "_mcp-test"), ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "MCP server failed to start");
            }
        });

        // ---------- schedules ----------
        api.MapGet("/schedules", (SchedulerService s) => s.ListAsync());
        api.MapPost("/schedules", (ScheduleJob job, SchedulerService s, CancellationToken ct) => s.SaveAsync(job, ct));
        api.MapDelete("/schedules/{id}", async (string id, SchedulerService s) => await s.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/schedules/{id}/run", (string id, SchedulerService s, CancellationToken ct) => s.RunNowAsync(id, ct));

        // ---------- memory ----------
        api.MapGet("/memory/{owner}", (string owner, IMemoryStore m) => m.ListAsync(owner));
        api.MapGet("/memory/{owner}/search", (string owner, string q, IMemoryStore m) => m.SearchAsync(new MemoryQuery(q, [owner, WellKnown.SharedMemoryOwner], 10)));
        api.MapPost("/memory", async (MemoryRecord rec, IMemoryStore m) => { await m.WriteAsync(rec); return rec; });
        api.MapDelete("/memory/item/{id}", async (string id, IMemoryStore m) => await m.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

        // ---------- hosts, usage, events ----------
        api.MapGet("/hosts", (HostService h) => h.List());
        api.MapGet("/usage", (UsageService u) => u.SummaryAsync());
        api.MapGet("/events/recent", (int? limit, IEventStore s) => s.RecentAsync(Math.Clamp(limit ?? 100, 1, 1000)));
        api.MapGet("/events", (HttpContext ctx, IEventBus bus, IEventStore store, long? after) => Sse.StreamAsync(ctx, bus, store, null, null, after));
    }
}

/// <summary>Server-Sent Events: replays stored events after <c>after</c>, then streams live ones.</summary>
public static class Sse
{
    public static async Task StreamAsync(HttpContext ctx, IEventBus bus, IEventStore store, Func<AgentEvent, bool>? filter, string? threadId, long? after)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        var ct = ctx.RequestAborted;
        var live = bus.StreamAsync(filter, ct).GetAsyncEnumerator(ct);
        long last = after ?? long.MaxValue;
        if (after is { } a)
        {
            foreach (var e in await store.ListAsync(threadId, null, a, 1000, ct))
            {
                if (filter is not null && !filter(e)) continue;
                await WriteAsync(ctx, e, ct);
                last = e.Id;
            }
        }
        await ctx.Response.Body.FlushAsync(ct);
        try
        {
            while (await live.MoveNextAsync())
            {
                // Skip stored events already replayed; transient events (Id 0, e.g. streaming text) always pass.
                if (after is not null && live.Current.Id > 0 && live.Current.Id <= last && last != long.MaxValue) continue;
                await WriteAsync(ctx, live.Current, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { await live.DisposeAsync(); }
    }

    private static async Task WriteAsync(HttpContext ctx, AgentEvent e, CancellationToken ct)
    {
        await ctx.Response.WriteAsync($"id: {e.Id}\nevent: {e.Type}\ndata: {JsonSerializer.Serialize(e, MarbotsJsonContext.Default.AgentEvent)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}

public static class WorkspaceFiles
{
    public static IReadOnlyList<WorkspaceFile> List(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f => !IsNoise(Path.GetRelativePath(root, f.FullName)))
            .Take(2000)
            .Select(f => new WorkspaceFile(Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc))
            .OrderBy(f => f.Path).ToList();
    }

    /// <summary>Hides tool caches and dependency folders from listings (they stay downloadable).</summary>
    private static bool IsNoise(string relative)
    {
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (part is "node_modules" or "__pycache__" or ".git" or ".venv" or "venv" or ".pytest_cache" or "bin" or "obj") return true;
        return false;
    }

    public static IResult Download(string root, string path)
    {
        string full;
        try { full = Marbots.Kernel.WorkspacePaths.Resolve(root, path); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        if (!File.Exists(full)) return Results.NotFound();
        var type = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".json" => "application/json",
            ".md" or ".txt" or ".py" or ".cs" or ".ps1" or ".sh" or ".csv" or ".yaml" or ".yml" or ".xml" => "text/plain; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream",
        };
        return Results.File(full, type, enableRangeProcessing: true);
    }
}

/// <summary>Optional API key protection for /api and /a2a (set Marbots:ApiKey). The in-process Blazor UI is unaffected.</summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
{
    /// <summary>Webhook and channel inbound endpoints authenticate with their own secret or signature.</summary>
    private static bool IsSelfAuthenticated(PathString path)
    {
        var p = path.Value ?? "";
        if (p.StartsWith("/api/v1/hooks/", StringComparison.Ordinal)) return true;
        return p.StartsWith("/api/v1/channels/", StringComparison.Ordinal) &&
               (p.EndsWith("/inbound", StringComparison.Ordinal) || p.EndsWith("/slack", StringComparison.Ordinal) ||
                p.EndsWith("/whatsapp", StringComparison.Ordinal) || p.EndsWith("/telegram", StringComparison.Ordinal));
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var key = config["Marbots:ApiKey"];
        var path = ctx.Request.Path;
        if (!string.IsNullOrEmpty(key) && (path.StartsWithSegments("/api") || path.StartsWithSegments("/a2a")) && !IsSelfAuthenticated(path))
        {
            var supplied = ctx.Request.Headers["X-Api-Key"].ToString();
            if (string.IsNullOrEmpty(supplied) && ctx.Request.Headers.Authorization.ToString() is { } auth && auth.StartsWith("Bearer ", StringComparison.Ordinal))
                supplied = auth[7..];
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes(key)))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Title = "Missing or invalid API key", Status = 401 });
                return;
            }
        }
        await next(ctx);
    }
}
