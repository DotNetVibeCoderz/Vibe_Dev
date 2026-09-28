using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;
using DotCode.Engine.Context;
using DotCode.Engine.Extensibility;
using DotCode.Engine.Hooks;
using DotCode.Engine.Mcp;
using DotCode.Engine.Permissions;
using DotCode.Engine.Sessions;
using DotCode.Engine.Tools;
using DotCode.Engine.Tools.Builtin;
using DotCode.Engine.Util;
using DotCode.Providers.Testing;

namespace DotCode.Engine.Agent;

public sealed record GitInfo(bool IsRepo, string? Branch, string? MainBranch, string? Status, string? RecentCommits);

/// <summary>Process-wide services shared by all sessions in one working directory: settings, model router,
/// extensions (skills/commands/agents/plugins), MCP connections, hooks, memory files and built-in tools.</summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    public RuntimeOptions Options { get; }
    public string Cwd { get; }
    public string ProjectRoot { get; }
    public Settings Settings { get; private set; }
    public SettingsLoader Loader { get; private set; }
    public ModelRouter Router { get; private set; }
    public ExtensionRegistry Extensions { get; private set; }
    public List<MemoryFile> Memory { get; private set; }
    public HookRunner Hooks { get; private set; }
    /// <summary>Opt-in hash-chained audit log (settings "audit").</summary>
    public Observability.AuditLog? Audit { get; private set; }
    /// <summary>Language servers for the LSP tool (started lazily).</summary>
    public Lsp.LspManager Lsp { get; private set; } = null!;
    public McpManager Mcp { get; } = new();
    public Task McpReady { get; private set; } = Task.CompletedTask;
    public BackgroundShellManager BackgroundShells { get; } = new();
    public GitInfo Git { get; }
    public List<Tool> BuiltinTools { get; }
    public string MainModelReference { get; set; }

    private AgentRuntime(RuntimeOptions options)
    {
        Options = options;
        Cwd = Path.GetFullPath(options.Cwd);
        Loader = new SettingsLoader(Cwd);
        ProjectRoot = Loader.ProjectRoot;
        Settings = Loader.Load(options.SettingsPath, options.SettingsJson);
        ApplyEnv(Settings);
        Router = new ModelRouter(Settings);
        Extensions = ExtensionRegistry.Load(ProjectRoot, Settings);
        Memory = MemoryLoader.Load(Cwd, ProjectRoot);
        Hooks = HookRunner.Create(Settings, Extensions.PluginHooks, Cwd);
        Audit = Observability.AuditLog.Create(Settings);
        var previousLsp = Lsp;
        Lsp = new Lsp.LspManager(Settings, ProjectRoot);
        if (previousLsp is not null) _ = previousLsp.DisposeAsync().AsTask();
        Observability.OtlpExporter.Start(Observability.OtelConfig.Resolve(Settings));
        Git = LoadGitInfo(Cwd);
        BuiltinTools = BuiltinToolset.Create(this);
        MainModelReference = options.Model ?? Router.DefaultMainModel();
    }

    public static AgentRuntime Create(RuntimeOptions options, bool connectMcp = true)
    {
        var runtime = new AgentRuntime(options);
        if (connectMcp && !options.NoMcp) runtime.McpReady = runtime.ConnectMcpAsync(CancellationToken.None);
        return runtime;
    }

    private async Task ConnectMcpAsync(CancellationToken ct)
    {
        var configs = Options.StrictMcpConfig
            ? new Dictionary<string, (McpServerConfig, string)>(StringComparer.OrdinalIgnoreCase)
            : McpManager.CollectConfigs(Settings, ProjectRoot, Extensions.PluginMcpServers);
        foreach (var file in Options.McpConfigs)
        {
            try
            {
                var json = File.Exists(file) ? await File.ReadAllTextAsync(file, ct).ConfigureAwait(false) : file;
                var cfg = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.McpConfigFile);
                if (cfg?.McpServers is not null)
                    foreach (var (n, s) in cfg.McpServers) configs[n] = (s, "cli");
            }
            catch (JsonException) { }
        }
        if (configs.Count == 0) return;
        await Mcp.ConnectAllAsync(configs, Cwd, ct).ConfigureAwait(false);
    }

    private static void ApplyEnv(Settings settings)
    {
        if (settings.Env is null) return;
        foreach (var (k, v) in settings.Env)
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k))) Environment.SetEnvironmentVariable(k, Providers.ConfigValue.Expand(v));
    }

    /// <summary>Re-reads settings, extensions, memory and hooks (after /config changes, plugin installs, /memory edits).</summary>
    public void Reload()
    {
        Loader = new SettingsLoader(Cwd);
        Settings = Loader.Load(Options.SettingsPath, Options.SettingsJson);
        ApplyEnv(Settings);
        Router = new ModelRouter(Settings);
        Extensions = ExtensionRegistry.Load(ProjectRoot, Settings);
        Memory = MemoryLoader.Load(Cwd, ProjectRoot);
        Hooks = HookRunner.Create(Settings, Extensions.PluginHooks, Cwd);
        Audit = Observability.AuditLog.Create(Settings);
        var previousLsp = Lsp;
        Lsp = new Lsp.LspManager(Settings, ProjectRoot);
        if (previousLsp is not null) _ = previousLsp.DisposeAsync().AsTask();
        Observability.OtlpExporter.Start(Observability.OtelConfig.Resolve(Settings));
        foreach (var tool in BuiltinTools)
        {
            if (tool is AgentTool at) at.Registry = Extensions;
            else if (tool is SkillTool st) st.Registry = Extensions;
        }
    }

    public PermissionMode InitialMode()
    {
        if (Options.DangerouslySkipPermissions) return PermissionMode.BypassPermissions;
        var mode = PermissionModes.Parse(Options.PermissionMode ?? Settings.Permissions?.DefaultMode);
        if (mode == PermissionMode.BypassPermissions && Settings.Permissions?.DisableBypassPermissionsMode == true) return PermissionMode.Default;
        return mode;
    }

    public ReasoningEffort InitialEffort() => (Options.Effort ?? Settings.Effort)?.ToLowerInvariant() switch
    {
        "off" or "none" => ReasoningEffort.Off,
        "low" => ReasoningEffort.Low,
        "high" => ReasoningEffort.High,
        "xhigh" or "max" => ReasoningEffort.XHigh,
        "medium" => ReasoningEffort.Medium,
        _ => ReasoningEffort.Medium,
    };

    public AgentSession CreateSession(string? model = null, bool persist = true)
    {
        var resolved = Router.Resolve(model ?? MainModelReference);
        if (Options.RecordTo is { } record)
            resolved = resolved with { Provider = new RecordingProvider(resolved.Provider, record) };
        var session = new AgentSession(this, Guid.NewGuid().ToString(), resolved, parent: null);
        if (persist && Options.PersistSession) session.Store = SessionStore.Create(Cwd, session.Id, resolved.Qualified);
        return session;
    }

    public AgentSession ResumeSession(string path, bool fork = false)
    {
        var loaded = SessionStore.Load(path);
        ResolvedModel resolved;
        try { resolved = Router.Resolve(Options.Model ?? loaded.Model ?? MainModelReference); }
        catch (InvalidOperationException) { resolved = Router.Resolve(MainModelReference); }
        var session = new AgentSession(this, fork ? Guid.NewGuid().ToString() : loaded.Id, resolved, parent: null) { Title = loaded.Title };
        session.Messages.AddRange(loaded.Messages);
        session.RestoreTodosFromHistory();
        if (Options.PersistSession)
        {
            if (fork)
            {
                session.Store = SessionStore.Create(Cwd, session.Id, resolved.Qualified);
                foreach (var m in loaded.Messages) session.Store.AppendMessage(m);
            }
            else session.Store = SessionStore.OpenForAppend(path);
        }
        return session;
    }

    private static GitInfo LoadGitInfo(string cwd)
    {
        if (ProcessRunner.TryRun("git", "rev-parse --is-inside-work-tree", cwd, 2000) != "true") return new GitInfo(false, null, null, null, null);
        var branch = ProcessRunner.TryRun("git", "rev-parse --abbrev-ref HEAD", cwd);
        var main = ProcessRunner.TryRun("git", "symbolic-ref --short refs/remotes/origin/HEAD", cwd)?.Replace("origin/", "")
                   ?? (ProcessRunner.TryRun("git", "rev-parse --verify --quiet main", cwd) is not null ? "main" : "master");
        var status = ProcessRunner.TryRun("git", "status --short", cwd);
        if (status is not null)
        {
            var lines = status.Split('\n');
            if (lines.Length > 40) status = string.Join('\n', lines.Take(40)) + $"\n... ({lines.Length - 40} more)";
        }
        var log = ProcessRunner.TryRun("git", "log --oneline -n 5", cwd);
        return new GitInfo(true, branch, main, string.IsNullOrWhiteSpace(status) ? "(clean)" : status, log);
    }

    public async ValueTask DisposeAsync()
    {
        BackgroundShells.KillAll();
        await Mcp.DisposeAsync().ConfigureAwait(false);
        await Lsp.DisposeAsync().ConfigureAwait(false);
        await Observability.OtlpExporter.FlushAsync().ConfigureAwait(false);
    }
}
