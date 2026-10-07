using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Marbots.Abstractions;
using Marbots.Providers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

public sealed class MarbotsOptions
{
    public string DataDirectory { get; set; } = "data";
    public List<ProviderConfig> Providers { get; set; } = [];
    public List<ModelProfile> ModelProfiles { get; set; } = [];
    public int MaxConcurrentRuns { get; set; } = 8;
    public int MaxParallelDelegations { get; set; } = 4;
    public int MaxDelegationDepth { get; set; } = 2;
    public int ApprovalTimeoutMinutes { get; set; } = 30;
    public List<string> SkillDirectories { get; set; } = [];
    public bool SeedStarterBots { get; set; } = true;
    /// <summary>
    /// Where SSH bootstrap finds agent-host binaries: marbots-host-&lt;rid&gt;.exe (win-x64, win-arm64) or
    /// marbots-host-&lt;rid&gt; (linux-x64, linux-arm64, osx-arm64, osx-x64). Defaults to data/host-packages.
    /// </summary>
    public string? HostPackagesDirectory { get; set; }

    /// <summary>Database: SQLite in the data directory by default, or PostgreSQL / SQL Server / MySQL.</summary>
    public Marbots.Storage.DatabaseOptions Database { get; set; } = new();

    /// <summary>Tenant whose rows this runtime reads and writes ("default" in single-tenant mode).</summary>
    public string TenantId { get; set; } = "default";

    /// <summary>Memory embeddings: "hash" (offline, default), "none", or "provider/model" for an embeddings endpoint.</summary>
    public string EmbeddingModel { get; set; } = "hash";
    /// <summary>Start with approvals skipped (server flag <c>--dangerously-skip-approvals</c>). Can be turned off at runtime.</summary>
    public bool DangerouslySkipApprovals { get; set; }

    /// <summary>Agent-host mutual TLS.</summary>
    public HostSecurityOptions HostSecurity { get; set; } = new();

    /// <summary>Remote push notifications (FCM, APNs, ntfy).</summary>
    public MarbotsPushOptions Push { get; set; } = new();

    /// <summary>OpenTelemetry export (traces, metrics, logs over OTLP).</summary>
    public MarbotsTelemetryOptions Telemetry { get; set; } = new();

    /// <summary>Run one isolated runtime per tenant (see docs: multi-tenant).</summary>
    public bool MultiTenant { get; set; }

    /// <summary>Sign-in: API keys (global and per tenant), optionally OIDC with roles.</summary>
    public MarbotsAuthOptions Auth { get; set; } = new();

    public string DataPath(params string[] parts) => Path.GetFullPath(Path.Combine([DataDirectory, .. parts]));

    /// <summary>A copy whose lists can be changed independently (used for per-tenant runtimes).</summary>
    public MarbotsOptions Clone()
    {
        var o = (MarbotsOptions)MemberwiseClone();
        o.Providers = [.. Providers];
        o.ModelProfiles = [.. ModelProfiles];
        o.SkillDirectories = [.. SkillDirectories];
        return o;
    }
}

/// <summary>
/// Local secret provider: values entered in the UI are encrypted with ASP.NET Core Data Protection
/// (DPAPI-protected keys on Windows). Falls back to configuration (Marbots:Secrets:NAME) and environment variables.
/// Secret values are never returned by any API — only names.
/// </summary>
public sealed class LocalSecretProvider : ISecretProvider
{
    private readonly IDataProtector _protector;
    private readonly IConfiguration _config;
    private readonly string _file;
    private readonly Lock _lock = new();
    private Dictionary<string, string> _values;

    public LocalSecretProvider(IDataProtectionProvider dp, MarbotsOptions options, IConfiguration config)
    {
        _protector = dp.CreateProtector("Marbots.Secrets.v1");
        _config = config;
        _file = options.DataPath("secrets.dat");
        _values = Load();
    }

    public string? Get(string name)
    {
        lock (_lock)
            if (_values.TryGetValue(name, out var v)) return v;
        return _config[$"Marbots:Secrets:{name}"] is { Length: > 0 } c ? c : Environment.GetEnvironmentVariable(name);
    }

    public IReadOnlyList<string> Names()
    {
        lock (_lock) return [.. _values.Keys.Order()];
    }

    public void Set(string name, string? value)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(value)) _values.Remove(name);
            else _values[name] = value;
            var json = JsonSerializer.Serialize(_values, MarbotsJsonContext.Default.DictionaryStringString);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, _protector.Protect(json));
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_file)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = _protector.Unprotect(File.ReadAllText(_file));
            return new(JsonSerializer.Deserialize(json, MarbotsJsonContext.Default.DictionaryStringString) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// Routes a bot's model setting to a provider. A setting can be a named profile ("coding"), a direct
/// "provider/model" pair ("azure/gpt-5.6-luna"), or empty/"default" which follows the workspace default model.
/// Unknown profiles, unconfigured providers and failing models fall back to the default.
/// Falls back to an offline mock if nothing is configured.
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    public const string DefaultProfile = "default";

    private readonly MarbotsOptions _options;
    private readonly ISecretProvider _secrets;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDocumentStore<ProviderConfig> _providerStore;
    private readonly IDocumentStore<ModelProfile> _profileStore;
    private readonly ILogger<ModelRouter> _log;
    private volatile State _state = new([], new Dictionary<string, IModelProvider>(), [], false);

    public MockProvider Mock { get; } = new();

    private sealed record State(IReadOnlyList<ModelProfile> Profiles, IReadOnlyDictionary<string, IModelProvider> Providers, IReadOnlyList<string> Choices, bool Configured);

    public ModelRouter(MarbotsOptions options, ISecretProvider secrets, IHttpClientFactory httpFactory,
        IDocumentStore<ProviderConfig> providerStore, IDocumentStore<ModelProfile> profileStore, ILogger<ModelRouter> log)
    {
        _options = options;
        _secrets = secrets;
        _httpFactory = httpFactory;
        _providerStore = providerStore;
        _profileStore = profileStore;
        _log = log;
    }

    public IReadOnlyList<ModelProfile> Profiles => _state.Profiles;
    public bool IsConfigured => _state.Configured;

    /// <summary>"provider/model" pairs offered in model pickers.</summary>
    public IReadOnlyList<string> Choices => _state.Choices;

    /// <summary>The workspace default model, used by every bot whose model is "default".</summary>
    public ModelProfile Default => Resolve(null).Profile;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _options.Providers) providers[p.Name] = p;
        foreach (var p in await _providerStore.ListAsync(ct))
        {
            // Stored settings win, but keep model lists declared in configuration.
            if (providers.TryGetValue(p.Name, out var cfg) && p.Models.Count == 0) p.Models = cfg.Models;
            if (providers.TryGetValue(p.Name, out cfg) && string.IsNullOrEmpty(p.ApiKey)) p.ApiKey = cfg.ApiKey;
            providers[p.Name] = p;
        }

        var instances = new Dictionary<string, IModelProvider>(StringComparer.OrdinalIgnoreCase) { ["mock"] = Mock };
        foreach (var p in providers.Values)
        {
            if (p.Kind == "mock") { instances[p.Name] = Mock; continue; }
            var key = ResolveKey(p);
            if (string.IsNullOrEmpty(key) && p.Kind == "azure-openai") continue;
            instances[p.Name] = new OpenAiCompatibleProvider(_httpFactory.CreateClient("marbots-llm"), new ProviderConfig { Name = p.Name, Kind = p.Kind, Endpoint = p.Endpoint, ApiKey = key });
        }

        var profiles = new Dictionary<string, ModelProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _options.ModelProfiles) profiles[p.Name] = p;
        foreach (var p in await _profileStore.ListAsync(ct)) profiles[p.Name] = p;
        var configured = providers.Values.Any(p => p.Kind != "mock" && instances.ContainsKey(p.Name));
        if (!profiles.TryGetValue(DefaultProfile, out var def) || !instances.ContainsKey(def.Provider))
        {
            var first = providers.Values.FirstOrDefault(p => p.Kind != "mock" && instances.ContainsKey(p.Name));
            profiles[DefaultProfile] = first is null
                ? new ModelProfile { Name = DefaultProfile, Provider = "mock", Model = "mock" }
                : new ModelProfile { Name = DefaultProfile, Provider = first.Name, Model = first.Models.FirstOrDefault() ?? (first.Kind == "azure-openai" ? "gpt-5-mini" : "gpt-4o-mini") };
        }

        // Choices offered in the UI: every model a provider declares plus every model a profile uses.
        var choices = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers.Values.Where(p => instances.ContainsKey(p.Name)))
            foreach (var m in p.Models) choices.Add($"{p.Name}/{m}");
        foreach (var p in profiles.Values.Where(p => instances.ContainsKey(p.Provider) && (!configured || p.Provider != "mock")))
            choices.Add($"{p.Provider}/{p.Model}");

        _state = new State([.. profiles.Values.OrderBy(p => p.Name != DefaultProfile).ThenBy(p => p.Name)], instances, [.. choices], configured);
        _log.LogInformation("Model router loaded {Providers} provider(s), {Profiles} profile(s), default model {Default}. Configured: {Configured}",
            instances.Count - 1, profiles.Count, Describe(profiles[DefaultProfile]), configured);
    }

    private string? ResolveKey(ProviderConfig p)
    {
        if (p.ApiKey is { Length: > 0 } k)
            return k.StartsWith("secret:", StringComparison.Ordinal) ? _secrets.Get(k[7..]) : k;
        return _secrets.Get($"provider:{p.Name}:apikey");
    }

    public static string Describe(ModelProfile p) => $"{p.Provider}/{p.Model}";

    public static bool IsDefaultSetting(string? setting) =>
        string.IsNullOrWhiteSpace(setting) || setting.Equals(DefaultProfile, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves a bot's model setting to the profile that will actually be used.</summary>
    public ModelResolution Resolve(string? setting)
    {
        var state = _state;
        var def = state.Profiles.FirstOrDefault(p => p.Name == DefaultProfile) ?? new ModelProfile { Name = DefaultProfile, Provider = "mock", Model = "mock" };
        if (IsDefaultSetting(setting)) return new(def, true, null);
        var named = state.Profiles.FirstOrDefault(p => p.Name.Equals(setting, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
            return state.Providers.ContainsKey(named.Provider) ? new(named, false, null)
                : new(def, true, $"Provider '{named.Provider}' of profile '{named.Name}' is not configured; using the default model.");
        var slash = setting!.IndexOf('/');
        if (slash > 0 && slash < setting.Length - 1)
        {
            var provider = setting[..slash];
            var model = setting[(slash + 1)..];
            if (state.Providers.ContainsKey(provider))
            {
                // Reuse a profile's limits/prices when one already targets this exact model.
                var twin = state.Profiles.FirstOrDefault(p => p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) && p.Model.Equals(model, StringComparison.OrdinalIgnoreCase)) ?? def;
                return new(new ModelProfile
                {
                    Name = setting, Provider = provider, Model = model,
                    MaxOutputTokens = twin.MaxOutputTokens, InputCostPerMTok = twin.InputCostPerMTok, OutputCostPerMTok = twin.OutputCostPerMTok,
                }, false, null);
            }
            return new(def, true, $"Provider '{provider}' is not configured; using the default model.");
        }
        return new(def, true, $"Unknown model '{setting}'; using the default model.");
    }

    /// <summary>Sets the workspace default model from a "provider/model" choice or a profile name.</summary>
    public async Task SetDefaultAsync(string choice, CancellationToken ct = default)
    {
        var target = Resolve(choice);
        if (target.Fallback && !IsDefaultSetting(choice)) throw new ArgumentException(target.Warning ?? $"Unknown model '{choice}'.");
        var p = target.Profile;
        await _profileStore.UpsertAsync(new ModelProfile
        {
            Name = DefaultProfile, Provider = p.Provider, Model = p.Model, MaxOutputTokens = p.MaxOutputTokens,
            InputCostPerMTok = p.InputCostPerMTok, OutputCostPerMTok = p.OutputCostPerMTok,
        }, ct);
        await ReloadAsync(ct);
    }

    public async Task<(ModelResponse Response, ModelProfile Profile)> CompleteAsync(string profileName, ModelRequest request, CancellationToken cancellationToken)
    {
        var state = _state;
        var first = Resolve(profileName);
        if (first.Warning is not null) _log.LogDebug("{Warning}", first.Warning);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ModelProfile>();
        queue.Enqueue(first.Profile);
        Exception? last = null;
        while (queue.TryDequeue(out var profile))
        {
            if (!visited.Add(Describe(profile))) continue;
            foreach (var f in profile.Fallbacks) queue.Enqueue(Resolve(f).Profile);
            // A bot's own model that fails always falls back to the workspace default last.
            if (profile.Name != DefaultProfile) queue.Enqueue(Resolve(null).Profile);
            if (!state.Providers.TryGetValue(profile.Provider, out var provider))
            {
                last = new InvalidOperationException($"Provider '{profile.Provider}' for model '{profile.Name}' is not configured.");
                continue;
            }
            request.Model = profile.Model;
            request.MaxOutputTokens ??= profile.MaxOutputTokens;
            try
            {
                return (await provider.CompleteAsync(request, cancellationToken), profile);
            }
            catch (Exception ex) when (ex is ModelProviderException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Model {Model} failed; trying fallbacks", Describe(profile));
                last = ex;
            }
        }
        throw last ?? new InvalidOperationException("No model profile available.");
    }
}

/// <summary>The model a setting resolves to. <see cref="Fallback"/> is true when the default model is used.</summary>
public sealed record ModelResolution(ModelProfile Profile, bool Fallback, string? Warning)
{
    public string Label => ModelRouter.Describe(Profile);
}

/// <summary>Persists events and fans them out to in-process handlers and bounded stream subscribers.</summary>
public sealed class EventBus(IEventStore store, ILogger<EventBus> log) : IEventBus
{
    private ImmutableArray<Action<AgentEvent>> _handlers = [];
    private ImmutableArray<Channel<AgentEvent>> _channels = [];

    public async ValueTask<AgentEvent> PublishAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.AppendAsync(evt, CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            log.LogWarning(ex, "Failed to persist event {Type}", evt.Type);
        }
        Fanout(evt);
        return evt;
    }

    public void PublishTransient(AgentEvent evt) => Fanout(evt);

    private void Fanout(AgentEvent evt)
    {
        foreach (var h in _handlers)
        {
            try { h(evt); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogDebug(ex, "Event handler failed"); }
        }
        foreach (var ch in _channels) ch.Writer.TryWrite(evt);
    }

    public IDisposable Subscribe(Action<AgentEvent> handler)
    {
        ImmutableInterlocked.Update(ref _handlers, a => a.Add(handler));
        return new Unsubscriber(() => ImmutableInterlocked.Update(ref _handlers, a => a.Remove(handler)));
    }

    /// <remarks>The subscription is registered eagerly so callers can replay history first without gaps.</remarks>
    public IAsyncEnumerable<AgentEvent> StreamAsync(Func<AgentEvent, bool>? filter, CancellationToken cancellationToken)
    {
        var ch = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        ImmutableInterlocked.Update(ref _channels, a => a.Add(ch));
        var reg = cancellationToken.Register(() => ImmutableInterlocked.Update(ref _channels, a => a.Remove(ch)));
        return ReadAsync(ch, filter, reg, cancellationToken);
    }

    private async IAsyncEnumerable<AgentEvent> ReadAsync(Channel<AgentEvent> ch, Func<AgentEvent, bool>? filter, CancellationTokenRegistration reg,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var e in ch.Reader.ReadAllAsync(cancellationToken))
                if (filter is null || filter(e)) yield return e;
        }
        finally
        {
            ImmutableInterlocked.Update(ref _channels, a => a.Remove(ch));
            await reg.DisposeAsync();
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); }
    }
}

/// <summary>Deterministic policy evaluation outside the LLM. Profiles map permission categories to decisions.</summary>
public sealed class PolicyEngine : IPolicyEngine
{
    private static readonly PolicyDecisionKind A = PolicyDecisionKind.Allow, D = PolicyDecisionKind.Deny, Q = PolicyDecisionKind.Ask;

    // Order: ReadOnly, WorkspaceWrite, Network, ProcessExecution, DestructiveFilesystem, ExternalCommunication, AgentControl, Admin
    private static readonly Dictionary<string, PolicyDecisionKind[]> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read-only"] = [A, D, A, D, D, D, D, D],
        ["workspace-write"] = [A, A, A, D, Q, Q, A, D],
        ["developer-safe"] = [A, A, A, Q, Q, Q, A, D],
        ["manager"] = [A, A, A, Q, Q, Q, A, Q],
        ["autonomous"] = [A, A, A, A, A, Q, A, Q],
    };

    private readonly ConcurrentDictionary<string, byte> _sessionGrants = new();

    public IReadOnlyList<string> Profiles => [.. Table.Keys];

    public bool SkipApprovals { get; set; }

    public PolicyDecision Evaluate(ActionRequest action, SecurityContext context)
    {
        var decision = EvaluateProfile(action, context);
        return decision.Kind == Q && SkipApprovals
            ? new(A, "Allowed without approval: approvals are skipped (dangerous mode).")
            : decision;
    }

    private PolicyDecision EvaluateProfile(ActionRequest action, SecurityContext context)
    {
        if (!Table.TryGetValue(context.Bot.PermissionProfile, out var row))
            row = Table["developer-safe"];
        var decision = row[(int)action.Category];
        if (decision == D)
            return new(D, $"Permission profile '{context.Bot.PermissionProfile}' does not allow {action.Category} actions.");
        if (decision == Q && _sessionGrants.ContainsKey(Key(context.ThreadId, context.Bot.Id, action.ToolName)))
            return new(A, "Allowed for this session by a previous approval.");
        if (decision == A && action.Risk == RiskLevel.Critical)
            return new(Q, "Critical-risk actions always require approval.");
        return decision == Q
            ? new(Q, $"{action.Category} actions need human approval under profile '{context.Bot.PermissionProfile}'.")
            : new(A, "Allowed by profile.");
    }

    public void GrantForSession(string threadId, string botId, string toolName) => _sessionGrants[Key(threadId, botId, toolName)] = 1;

    private static string Key(string thread, string bot, string tool) => $"{thread}|{bot}|{tool}";
}

/// <summary>Human-in-the-loop approvals. Runs block on a TaskCompletionSource until resolved or expired.</summary>
public sealed class ApprovalService(IDocumentStore<ApprovalRequest> store, IDocumentStore<WorkspaceSettings> settings,
    IEventBus bus, IPolicyEngine policy, MarbotsOptions options)
{
    public const string SkipActor = "dangerously-skip-approvals";
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalRequest>> _waiters = new();

    public bool SkipApprovals => policy.SkipApprovals;

    /// <summary>Loads the persisted mode; the server flag / config forces it on at startup.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync(WorkspaceSettings.SingletonId, ct);
        policy.SkipApprovals = options.DangerouslySkipApprovals || s?.DangerouslySkipApprovals == true;
    }

    /// <summary>Turns dangerous mode on or off. Turning it on also approves everything currently pending.</summary>
    /// <summary>Current workspace settings (never null).</summary>
    public async Task<WorkspaceSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await settings.GetAsync(WorkspaceSettings.SingletonId, ct) ?? new WorkspaceSettings();

    private async Task SaveSettingsAsync(Action<WorkspaceSettings> change, string by, CancellationToken ct)
    {
        var s = await GetSettingsAsync(ct);
        change(s);
        s.ChangedBy = by;
        s.ChangedAt = DateTimeOffset.UtcNow;
        await settings.UpsertAsync(s, ct);
    }

    /// <summary>Auto: Boss Man delegates freely. Suggest: every delegation plan needs the user's approval first.</summary>
    public async Task SetDelegationModeAsync(DelegationMode mode, string by, CancellationToken ct = default)
    {
        await SaveSettingsAsync(s => s.Delegation = mode, by, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.SettingsChanged, Message = $"Delegation mode is now {mode}, by {by}", Data = "delegation:" + mode }, ct);
    }

    public async Task SetAutoRollbackSkillsAsync(bool on, string by, CancellationToken ct = default) =>
        await SaveSettingsAsync(s => s.AutoRollbackSkills = on, by, ct);

    public async Task SetSkipApprovalsAsync(bool skip, string by, CancellationToken ct = default)
    {
        policy.SkipApprovals = skip;
        await SaveSettingsAsync(s => s.DangerouslySkipApprovals = skip, by, ct);
        await bus.PublishAsync(new AgentEvent
        {
            Type = EventTypes.SettingsChanged, Message = skip ? $"Approvals are now skipped (dangerous mode), by {by}" : $"Approvals are required again, by {by}",
            Data = skip ? "skip-approvals:on" : "skip-approvals:off",
        }, ct);
        if (!skip) return;
        foreach (var pending in await PendingAsync(ct))
            await ResolveAsync(pending.Id, true, ApprovalScope.Once, SkipActor, ct);
    }

    public async Task<ApprovalRequest> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        request.Id = Ids.New("apr");
        if (policy.SkipApprovals)
        {
            // Still recorded for the audit trail, but never waits for a human.
            request.State = ApprovalState.Approved;
            request.ResolvedBy = SkipActor;
            request.ResolvedAt = DateTimeOffset.UtcNow;
            await store.UpsertAsync(request, ct);
            await bus.PublishAsync(new AgentEvent
            {
                Type = EventTypes.ApprovalResolved, BotId = request.BotId, TaskId = request.TaskId, ThreadId = request.ThreadId,
                Message = $"{request.ToolName} auto-approved (approvals skipped)", Data = request.Id,
            }, ct);
            return request;
        }
        var tcs = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[request.Id] = tcs;
        await store.UpsertAsync(request, ct);
        await bus.PublishAsync(new AgentEvent
        {
            Type = EventTypes.ApprovalRequested, BotId = request.BotId, TaskId = request.TaskId, ThreadId = request.ThreadId,
            Message = $"{request.ToolName}: {request.Reason}", Data = request.Id,
        }, ct);
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(options.ApprovalTimeoutMinutes), ct);
        }
        catch (TimeoutException)
        {
            request.State = ApprovalState.Expired;
            request.ResolvedAt = DateTimeOffset.UtcNow;
            await store.UpsertAsync(request, CancellationToken.None);
            return request;
        }
        finally
        {
            _waiters.TryRemove(request.Id, out _);
        }
    }

    public async Task<ApprovalRequest?> ResolveAsync(string id, bool approved, ApprovalScope scope, string by, CancellationToken ct = default)
    {
        var req = await store.GetAsync(id, ct);
        if (req is null || req.State != ApprovalState.Pending) return req;
        req.State = approved ? ApprovalState.Approved : ApprovalState.Rejected;
        req.Scope = scope;
        req.ResolvedBy = by;
        req.ResolvedAt = DateTimeOffset.UtcNow;
        await store.UpsertAsync(req, ct);
        if (approved && scope == ApprovalScope.Session) policy.GrantForSession(req.ThreadId, req.BotId, req.ToolName);
        await bus.PublishAsync(new AgentEvent
        {
            Type = EventTypes.ApprovalResolved, BotId = req.BotId, TaskId = req.TaskId, ThreadId = req.ThreadId,
            Message = $"{req.ToolName} {(approved ? "approved" : "rejected")} by {by}", Data = req.Id,
        }, ct);
        if (_waiters.TryGetValue(id, out var tcs)) tcs.TrySetResult(req);
        return req;
    }

    public async Task<IReadOnlyList<ApprovalRequest>> PendingAsync(CancellationToken ct = default) =>
        (await store.ListAsync(ct)).Where(a => a.State == ApprovalState.Pending).OrderBy(a => a.CreatedAt).ToList();

    public async Task<IReadOnlyList<ApprovalRequest>> AllAsync(CancellationToken ct = default) =>
        (await store.ListAsync(ct)).OrderByDescending(a => a.CreatedAt).ToList();

    /// <summary>Approvals that outlived their waiting run (e.g. after a restart) can never be honoured.</summary>
    public async Task ExpireOrphansAsync(CancellationToken ct)
    {
        foreach (var a in await store.ListAsync(ct))
        {
            if (a.State != ApprovalState.Pending || _waiters.ContainsKey(a.Id)) continue;
            a.State = ApprovalState.Expired;
            a.ResolvedAt = DateTimeOffset.UtcNow;
            await store.UpsertAsync(a, ct);
        }
    }
}
