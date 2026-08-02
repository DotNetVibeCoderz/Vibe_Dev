// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Abstractions;
using AutoCode.Core.Agents;
using AutoCode.Core.Configuration;
using AutoCode.Core.Context;
using AutoCode.Core.Cost;
using AutoCode.Core.Hooks;
using AutoCode.Core.Memory;
using AutoCode.Core.Permissions;
using AutoCode.Core.Plugins;
using AutoCode.Core.Prompts;
using AutoCode.Core.Runtime;
using AutoCode.Core.Sessions;
using AutoCode.Core.Skills;
using AutoCode.Mcp;
using AutoCode.Providers;
using AutoCode.Providers.Onnx;
using AutoCode.Tools;
using Microsoft.Extensions.AI;

namespace AutoCode.Cli;

/// <summary>
/// Composition root for a run: resolves configuration, brings up the provider, tools, MCP servers,
/// skills, subagents and hooks, and hands back a ready <see cref="AgentLoop"/>.
///
/// EN: everything is wired once, here, and nothing reaches for a global afterwards. That is what
/// makes a session reproducible — and what lets <c>/model</c> rebuild the loop mid-conversation
/// without leaking the previous provider's state.
/// ID: seluruh komponen dirangkai sekali di sini dan tidak ada yang memakai state global setelahnya.
/// Itulah yang membuat sesi dapat direproduksi, dan membuat <c>/model</c> bisa membangun ulang loop
/// di tengah percakapan tanpa menyisakan state provider sebelumnya.
/// </summary>
public sealed class AutoCodeSession : IAsyncDisposable
{
    private readonly List<IChatClient> _ownedClients = [];
    private SemanticCodeIndex? _index;
    private IEmbeddingGenerator<string, Embedding<float>>? _embeddings;

    private AutoCodeSession(
        AutoCodeOptions options,
        ProviderProfile profile,
        string workspaceRoot,
        IAgentUserInterface ui)
    {
        Options = options;
        Profile = profile;
        WorkspaceRoot = workspaceRoot;
        Ui = ui;
    }

    public AutoCodeOptions Options { get; }
    public ProviderProfile Profile { get; private set; }
    public string WorkspaceRoot { get; }
    public IAgentUserInterface Ui { get; }

    public ToolRegistry Tools { get; private set; } = null!;
    public PermissionEngine Permissions { get; private set; } = null!;
    public HookRunner Hooks { get; private set; } = null!;
    public CostTracker Cost { get; private set; } = null!;
    public SessionStore Store { get; private set; } = null!;
    public Session Session { get; private set; } = null!;
    public AgentLoop Loop { get; private set; } = null!;
    public AgentServices Services { get; private set; } = null!;
    public McpServerManager McpServers { get; } = new();
    public IReadOnlyList<Skill> Skills { get; private set; } = [];
    public IReadOnlyList<ContextFile> ContextFiles { get; private set; } = [];
    public IReadOnlyList<LoadedPlugin> Plugins { get; private set; } = [];
    public IReadOnlyDictionary<string, AgentDefinition> Agents { get; private set; } = new Dictionary<string, AgentDefinition>();
    public AgentTeamCoordinator? Teams { get; private set; }
    public IReadOnlyList<McpConnectionResult> McpConnections { get; private set; } = [];

    /// <summary>Brings a full session up, ready to take a turn.</summary>
    public static async Task<AutoCodeSession> CreateAsync(
        CommandLineOptions cli,
        IAgentUserInterface ui,
        CancellationToken cancellationToken)
    {
        var start = cli.WorkingDirectory is { Length: > 0 } requested
            ? Path.GetFullPath(requested)
            : Environment.CurrentDirectory;

        var workspaceRoot = ConfigurationLoader.DiscoverWorkspaceRoot(start);
        var options = ConfigurationLoader.Load(workspaceRoot);

        ApplyCommandLineOverrides(options, cli);

        var profile = options.ResolveActiveProfile()
            ?? throw new InvalidOperationException(
                "No LLM provider is configured. Run 'autocode config init', or set an API key such as " +
                "OPENAI_API_KEY, ANTHROPIC_API_KEY, GEMINI_API_KEY or DEEPSEEK_API_KEY.");

        if (cli.Model is { Length: > 0 } model)
            profile.Model = model;

        var session = new AutoCodeSession(options, profile, workspaceRoot, ui);
        await session.InitializeAsync(cli, cancellationToken).ConfigureAwait(false);

        return session;
    }

    private async Task InitializeAsync(CommandLineOptions cli, CancellationToken cancellationToken)
    {
        Plugins = PluginLoader.Load(WorkspaceRoot, Options);
        foreach (var plugin in Plugins)
            PluginLoader.Apply(plugin, Options);

        Permissions = new PermissionEngine(Options);
        Hooks = new HookRunner(Options);
        Cost = new CostTracker(Profile);
        Store = new SessionStore(WorkspaceRoot);
        Services = new AgentServices(Options, WorkspaceRoot, Ui);

        Tools = ToolRegistry.CreateDefault(Options);

        McpConnections = await McpServers
            .ConnectAsync(Options, Tools, WorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);

        Skills = SkillLoader.Discover(WorkspaceRoot, Options);
        foreach (var plugin in Plugins)
            Skills = [.. Skills.Concat(plugin.Skills)];

        var agents = new Dictionary<string, AgentDefinition>(
            AgentDefinitionLoader.Discover(WorkspaceRoot, Options), StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in Plugins)
        {
            foreach (var agent in plugin.Agents)
                agents[agent.Name] = agent;
        }

        Agents = agents;

        InitializeSemanticIndex();

        Session = await ResolveSessionAsync(cli, cancellationToken).ConfigureAwait(false);

        Services.Subagents = new SubagentDispatcher(
            Agents, ResolveChatClient, Tools, Permissions, Hooks, Services);

        if (Options.Teams.Count > 0)
            Teams = new AgentTeamCoordinator(Options.Teams, Services.Subagents);

        ContextFiles = cli.NoContextFiles
            ? []
            : ContextFileLoader.Discover(WorkspaceRoot, Environment.CurrentDirectory, Options);

        RebuildLoop();

        await Hooks.RunAsync(
            HookEvents.SessionStart,
            null,
            new System.Text.Json.Nodes.JsonObject
            {
                ["session_id"] = Session.Id,
                ["workspace"] = WorkspaceRoot,
                ["model"] = Profile.Model,
            },
            WorkspaceRoot,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the loop from the current provider, tools and context. Called at start-up and again
    /// whenever <c>/model</c>, <c>/provider</c> or <c>/permissions</c> changes something it depends on.
    /// </summary>
    public void RebuildLoop()
    {
        var chatClient = ResolveChatClient(Profile.Name, Profile.Model);
        _ownedClients.Add(chatClient);

        var summarizer = ProviderFactory.CreateSmallChatClient(Profile);
        _ownedClients.Add(summarizer);

        var systemPrompt = new SystemPromptBuilder().Build(
            Options,
            Profile,
            WorkspaceRoot,
            Tools.Tools,
            ContextFiles,
            Permissions.Mode,
            [.. Agents.Values.Select(a => $"{a.Name} — {a.Description}")],
            [.. Skills.Select(s => $"/{s.Name} — {s.Description}")]);

        Loop = new AgentLoop(new AgentLoopContext
        {
            ChatClient = chatClient,
            Tools = Tools,
            Permissions = Permissions,
            Hooks = Hooks,
            Services = Services,
            Options = Options,
            Profile = Profile,
            Cost = Cost,
            Session = Session,
            SystemPrompt = systemPrompt,
            Compactor = new ContextCompactor(summarizer),
        });
    }

    /// <summary>Switches the active provider profile and rebuilds the loop around it.</summary>
    public bool SwitchProvider(string providerName)
    {
        if (!Options.Providers.TryGetValue(providerName, out var profile))
            return false;

        Options.ActiveProvider = providerName;
        Profile = profile;
        Cost = new CostTracker(profile);
        RebuildLoop();

        return true;
    }

    /// <summary>Switches the model on the current provider and rebuilds the loop.</summary>
    public void SwitchModel(string modelId)
    {
        Profile.Model = modelId;
        RebuildLoop();
    }

    private IChatClient ResolveChatClient(string? providerName, string? modelId)
    {
        var profile = providerName is { Length: > 0 } && Options.Providers.TryGetValue(providerName, out var named)
            ? named
            : Profile;

        return ProviderFactory.CreateChatClient(profile, modelId is { Length: > 0 } ? modelId : profile.Model);
    }

    private async Task<Session> ResolveSessionAsync(CommandLineOptions cli, CancellationToken cancellationToken)
    {
        if (cli.ResumeSessionId is { Length: > 0 } id)
        {
            var resumed = await Store.LoadAsync(id, cancellationToken).ConfigureAwait(false);
            if (resumed is not null)
                return Restore(resumed);
        }

        if (cli.Continue)
        {
            var latest = await Store.LoadLatestAsync(cancellationToken).ConfigureAwait(false);
            if (latest is not null)
                return Restore(latest);
        }

        return new Session
        {
            WorkspaceRoot = WorkspaceRoot,
            ProviderName = Profile.Name,
            ModelId = Profile.Model,
        };
    }

    /// <summary>Re-seeds the cost tracker so a resumed session's totals continue rather than restart.</summary>
    private Session Restore(Session session)
    {
        session.WorkspaceRoot = WorkspaceRoot;
        return session;
    }

    /// <summary>
    /// Resolves the embedding backend and, when one is available, stands up the semantic index.
    ///
    /// EN: the ONNX case is wired here rather than in <c>EmbeddingFactory</c> so that the native
    /// ONNX Runtime dependency stays confined to the composition root. Anything embedding
    /// <c>AutoCode.Core</c> can leave that project out entirely.
    /// ID: kasus ONNX dirangkai di sini, bukan di <c>EmbeddingFactory</c>, agar dependensi native
    /// ONNX Runtime tetap terkurung di composition root.
    /// </summary>
    private void InitializeSemanticIndex()
    {
        if (!Options.EnableSemanticIndex)
            return;

        EmbeddingBackend = Options.Embeddings.Resolve(Profile);

        if (EmbeddingBackend.Kind == EmbeddingProviderKind.None)
            return;

        try
        {
            var embeddings = EmbeddingBackend.Kind == EmbeddingProviderKind.Onnx
                ? OnnxEmbeddingGenerator.Create(EmbeddingBackend)
                : EmbeddingFactory.Create(EmbeddingBackend);

            if (embeddings is null)
                return;

            _embeddings = embeddings;
            _index = new SemanticCodeIndex(WorkspaceRoot, embeddings, EmbeddingBackend.Dimensions);
            Services.SemanticIndex = _index;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            // A misconfigured index must not stop the session; CodeSearch and /index report it.
            EmbeddingError = ex.Message;
        }
    }

    /// <summary>The resolved embedding settings, for <c>/status</c> and <c>doctor</c>.</summary>
    public EmbeddingOptions? EmbeddingBackend { get; private set; }

    /// <summary>Why the embedding backend could not be created, when it could not.</summary>
    public string? EmbeddingError { get; private set; }

    /// <summary>Builds the semantic index, reporting progress as it goes.</summary>
    public async Task<string> BuildIndexAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!Options.EnableSemanticIndex)
            return "The semantic index is disabled. Set enableSemanticIndex to true in settings.";

        if (EmbeddingError is { Length: > 0 } error)
            return $"The embedding backend could not start: {error}";

        if (_index is null)
        {
            return
                $"No embedding backend is configured. Provider '{Profile.Name}' does not supply one, so set " +
                "embeddings.kind in settings — \"Ollama\" for a local server, or \"Onnx\" for a local model file.";
        }

        await _index.BuildAsync(progress, cancellationToken).ConfigureAwait(false);

        return $"Index ready: {_index.ChunkCount:N0} chunks, {_index.Dimensions} dimensions " +
               $"({EmbeddingBackend?.Describe()}).";
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        Options.PersistSessions ? Store.SaveAsync(Session, cancellationToken) : Task.CompletedTask;

    private static void ApplyCommandLineOverrides(AutoCodeOptions options, CommandLineOptions cli)
    {
        if (cli.Provider is { Length: > 0 } provider)
        {
            if (!options.Providers.ContainsKey(provider) && ProviderPresets.TryCreate(provider) is { } preset)
                options.Providers[provider] = preset;

            options.ActiveProvider = provider;
        }

        if (cli.PermissionMode is { } mode)
            options.PermissionMode = mode;

        if (cli.Language is { Length: > 0 } language)
            options.Language = language;

        if (cli.AllowedTools.Count > 0)
            options.Permissions.Allow.AddRange(cli.AllowedTools);

        if (cli.DisallowedTools.Count > 0)
            options.DisabledTools.AddRange(cli.DisallowedTools);
    }

    public async ValueTask DisposeAsync()
    {
        await McpServers.DisposeAsync().ConfigureAwait(false);

        foreach (var client in _ownedClients)
            client.Dispose();

        _ownedClients.Clear();
        _index?.Dispose();
        _embeddings?.Dispose();
        Services?.Dispose();
    }
}
