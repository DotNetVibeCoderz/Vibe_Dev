using Marbots.Abstractions;
using Marbots.Kernel;
using Marbots.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Marbots.Runtime;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the whole Marbots runtime (storage, providers, kernel, orchestration, scheduler).</summary>
    public static IServiceCollection AddMarbotsRuntime(this IServiceCollection services, MarbotsOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(_ => MarbotsDatabase.Create(options.Database, options.DataPath("marbots.db"), options.TenantId));
        services.AddSingleton<IEmbeddingProvider>(sp => EmbeddingProviders.Create(options, sp));
        var ctx = MarbotsJsonContext.Default;
        AddDocs(services, "bot", ctx.BotDefinition, b => b.Id);
        AddDocs(services, "template", ctx.BotTemplate, t => t.Id);
        AddDocs(services, "thread", ctx.ChatThread, t => t.Id);
        AddDocs(services, "task", ctx.TaskRecord, t => t.Id);
        AddDocs(services, "approval", ctx.ApprovalRequest, a => a.Id);
        AddDocs(services, "schedule", ctx.ScheduleJob, j => j.Id);
        AddDocs(services, "mcp", ctx.McpServerConfig, m => m.Id);
        AddDocs(services, "provider", ctx.ProviderConfig, p => p.Name);
        AddDocs(services, "modelprofile", ctx.ModelProfile, p => p.Name);
        AddDocs(services, "settings", ctx.WorkspaceSettings, s => s.Id);
        AddDocs(services, "channel", ctx.ChannelConfig, c => c.Id);
        AddDocs(services, "channelconv", ctx.ChannelConversation, c => c.Id);
        AddDocs(services, "trigger", ctx.TriggerConfig, t => t.Id);
        AddDocs(services, "skillstats", ctx.SkillStats, s => s.Id);
        AddDocs(services, "host", ctx.HostRecord, h => h.Id);
        AddDocs(services, "hostenroll", ctx.HostEnrollment, e => e.Id);
        AddDocs(services, "threadhost", ctx.ThreadHost, t => t.Id);
        AddDocs(services, "pushdevice", ctx.PushDevice, d => d.Id);
        services.AddSingleton<IMessageStore, MessageStore>();
        services.AddSingleton<IEventStore, EventStore>();
        services.AddSingleton<IMemoryStore>(sp => new MemoryStore(sp.GetRequiredService<MarbotsDatabase>(), sp.GetRequiredService<IEmbeddingProvider>()));

        services.AddHttpClient("marbots-llm", c => c.Timeout = TimeSpan.FromMinutes(6));
        services.AddHttpClient("marbots-mcp", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient(ChannelContext.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddHttpClient(PushService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<LocalSecretProvider>();
        services.AddSingleton<ISecretProvider>(sp => sp.GetRequiredService<LocalSecretProvider>());
        services.AddSingleton<ModelRouter>();
        services.AddSingleton<IModelRouter>(sp => sp.GetRequiredService<ModelRouter>());
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IPolicyEngine, PolicyEngine>();
        services.AddSingleton<ApprovalService>();
        services.AddSingleton<SkillRegistry>();
        services.AddSingleton<McpManager>();
        services.AddSingleton<TodoBoard>();

        foreach (var f in KernelCatalog.CreateDefault()) services.AddSingleton<IKernelFunction>(f);
        services.AddSingleton<IKernelFunction, LoadSkillFunction>();
        services.AddSingleton<IKernelFunction, ReadSkillFileFunction>();
        services.AddSingleton<IKernelFunction, ListBotsFunction>();
        services.AddSingleton<IKernelFunction, DelegateTasksFunction>();
        services.AddSingleton<IKernelFunction, ListTemplatesFunction>();
        services.AddSingleton<IKernelFunction, CreateBotFunction>();
        services.AddSingleton<IKernelFunction, ListHostsFunction>();
        services.AddSingleton<IKernelFunction, ListMcpCatalogFunction>();
        services.AddSingleton<IKernelFunction, InstallMcpFunction>();
        services.AddSingleton<IKernelFunction, ListSkillCatalogFunction>();
        services.AddSingleton<IKernelFunction, InstallSkillFunction>();
        services.AddSingleton<IKernelFunction, SpawnSubagentsFunction>();
        services.AddSingleton<IKernelFunction, ScheduleTaskFunction>();
        services.AddSingleton<IKernelFunction, GetTaskFunction>();

        services.AddSingleton<BotRegistry>();
        services.AddSingleton<TemplateService>();
        services.AddSingleton<ContextManager>();
        services.AddSingleton<ToolAssembler>();
        services.AddSingleton<AgentRuntime>();
        services.AddSingleton<AutoLearnService>();
        services.AddSingleton<MarbotsEngine>();
        services.AddSingleton<BotPackageService>();
        services.AddSingleton<HostService>();
        services.AddSingleton<HostCertificateAuthority>();
        services.AddSingleton<HostRegistry>();
        services.AddSingleton<HostConnectionManager>();
        services.AddSingleton<PlacementService>();
        services.AddSingleton<HostBootstrapper>();
        services.AddSingleton<SchedulerService>();
        services.AddHostedService<MarbotsBootstrapper>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());

        // Phase 3: triggers and channels
        services.AddSingleton<SkillEvaluator>();
        services.AddHostedService(sp => sp.GetRequiredService<SkillEvaluator>());
        services.AddSingleton<TriggerService>();
        services.AddHostedService(sp => sp.GetRequiredService<TriggerService>());
        services.AddSingleton<ChannelContext>();
        services.AddSingleton<IChannelAdapter, WebChatAdapter>();
        services.AddSingleton<IChannelAdapter, WebhookChannelAdapter>();
        services.AddSingleton<IChannelAdapter, TelegramAdapter>();
        services.AddSingleton<IChannelAdapter, SlackAdapter>();
        services.AddSingleton<IChannelAdapter, WhatsAppAdapter>();
        services.AddSingleton<IChannelAdapter, DiscordAdapter>();
        services.AddSingleton<IChannelAdapter, EmailAdapter>();
        services.AddSingleton<ChannelGateway>();
        services.AddHostedService(sp => sp.GetRequiredService<ChannelGateway>());
        services.AddHostedService<TelegramPoller>();
        services.AddHostedService<EmailPoller>();
        services.AddSingleton<PushService>();
        services.AddHostedService(sp => sp.GetRequiredService<PushService>());
        return services;
    }

    private static void AddDocs<T>(IServiceCollection s, string kind, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, Func<T, string> id) where T : class =>
        s.AddSingleton<IDocumentStore<T>>(sp => new DocumentStore<T>(sp.GetRequiredService<MarbotsDatabase>(), kind, info, id));
}

/// <summary>Seeds catalogues, ensures Boss Man exists, loads providers and recovers interrupted work at startup.</summary>
public sealed class MarbotsBootstrapper(
    TemplateService templates, BotRegistry registry, ModelRouter router, ApprovalService approvals,
    MarbotsEngine engine, IDocumentStore<McpServerConfig> mcp, McpManager mcpManager) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await templates.SeedAsync(cancellationToken);
        await McpCatalog.SeedAsync(mcp, cancellationToken);
        await registry.EnsureSeedAsync(cancellationToken);
        await router.ReloadAsync(cancellationToken);
        await approvals.ExpireOrphansAsync(cancellationToken);
        await approvals.InitializeAsync(cancellationToken);
        await engine.RecoverAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        engine.Shutdown();
        await mcpManager.ShutdownAsync();
    }
}

/// <summary>Curated MCP Gallery entries. Installing an entry turns it into a usable server.</summary>
public static class McpCatalog
{
    public static IReadOnlyList<McpServerConfig> Entries { get; } =
    [
        new() { Id = "filesystem", Name = "Filesystem", Description = "Read/write/search files inside the project workspace (official reference server).", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem", "{workspace}"], Trust = "Trusted Publisher", PermissionProfile = "developer" },
        new() { Id = "memory-graph", Name = "Knowledge Graph Memory", Description = "Persistent entity/relation knowledge graph (official reference server).", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-memory"], Trust = "Trusted Publisher", PermissionProfile = "developer" },
        new() { Id = "sequential-thinking", Name = "Sequential Thinking", Description = "Structured step-by-step reasoning scratchpad.", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-sequential-thinking"], Trust = "Trusted Publisher", PermissionProfile = "readonly" },
        new() { Id = "everything", Name = "Everything (test server)", Description = "Reference server that exercises every MCP feature. Useful for testing.", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-everything"], Trust = "Trusted Publisher", PermissionProfile = "readonly" },
        new() { Id = "playwright", Name = "Playwright Browser", Description = "Drive a real browser: navigate, click, fill forms, take snapshots.", Command = "npx", Args = ["-y", "@playwright/mcp@latest", "--headless", "--output-dir", "{workspace}"], Trust = "Trusted Publisher", PermissionProfile = "network" },
        new() { Id = "github", Name = "GitHub", Description = "Issues, pull requests, repositories and code search. Needs secret GITHUB_TOKEN.", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-github"], Env = new() { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "secret:GITHUB_TOKEN" }, Trust = "Trusted Publisher", PermissionProfile = "external" },
        new() { Id = "fetch", Name = "Fetch", Description = "Fetch URLs and convert pages to markdown (Python, via uvx).", Command = "uvx", Args = ["mcp-server-fetch"], Trust = "Trusted Publisher", PermissionProfile = "network" },
        new() { Id = "time", Name = "Time", Description = "Current time and time-zone conversion (Python, via uvx).", Command = "uvx", Args = ["mcp-server-time"], Trust = "Trusted Publisher", PermissionProfile = "readonly" },
        new() { Id = "git", Name = "Git", Description = "Inspect and operate on the git repository in the workspace (Python, via uvx).", Command = "uvx", Args = ["mcp-server-git", "--repository", "{workspace}"], Trust = "Trusted Publisher", PermissionProfile = "developer" },
        new() { Id = "context7", Name = "Context7 Docs", Description = "Up-to-date library documentation and code examples for coding bots.", Command = "npx", Args = ["-y", "@upstash/context7-mcp"], Trust = "Community", PermissionProfile = "network" },
    ];

    public static readonly HashSet<string> InstalledByDefault = ["filesystem", "sequential-thinking"];

    public static async Task SeedAsync(IDocumentStore<McpServerConfig> store, CancellationToken ct)
    {
        foreach (var e in Entries)
        {
            if (await store.GetAsync(e.Id, ct) is not null) continue;
            var copy = new McpServerConfig
            {
                Id = e.Id, Name = e.Name, Description = e.Description, Transport = e.Transport, Command = e.Command, Args = [.. e.Args],
                Env = new(e.Env), Url = e.Url, Trust = e.Trust, PermissionProfile = e.PermissionProfile, Enabled = true,
                IsCatalogEntry = !InstalledByDefault.Contains(e.Id),
            };
            await store.UpsertAsync(copy, ct);
        }
    }
}
