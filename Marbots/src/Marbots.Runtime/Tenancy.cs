using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Marbots.Abstractions;
using Marbots.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

/// <summary>How callers sign in. Keys work in every mode; OIDC adds single sign-on for the UI and bearer tokens for the API.</summary>
public sealed class MarbotsAuthOptions
{
    /// <summary>"apikey" (default) or "oidc".</summary>
    public string Mode { get; set; } = "apikey";
    /// <summary>Require signing in to the web UI. Defaults to on in multi-tenant mode and with OIDC.</summary>
    public bool? RequireUiLogin { get; set; }
    /// <summary>OIDC issuer, e.g. https://login.microsoftonline.com/&lt;tenant&gt;/v2.0 or https://accounts.google.com.</summary>
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    /// <summary>Secret name (Marbots:Secrets / environment) holding the OIDC client secret.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Expected audience of API bearer tokens (defaults to <see cref="ClientId"/>).</summary>
    public string? Audience { get; set; }
    /// <summary>Claim that names the user's tenant (optional; members are also looked up by e-mail/subject).</summary>
    public string TenantClaim { get; set; } = "tenant";
    /// <summary>Claim holding roles; a value of Viewer/Operator/Admin/Owner is used when there is no membership.</summary>
    public string RoleClaim { get; set; } = "roles";
    /// <summary>E-mail addresses or subjects that may manage all tenants.</summary>
    public List<string> PlatformAdmins { get; set; } = [];
    /// <summary>Optional HS256 key (≥ 32 chars) for bearer tokens issued by your own gateway/IdP bridge.</summary>
    public string? JwtSigningKey { get; set; }
    public string? JwtIssuer { get; set; }
}

public static class Tenants
{
    public const string Default = "default";
    public const string Platform = "_platform";
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{1,39}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    /// <summary>Required role for an API call; null means "any authenticated caller".</summary>
    public static TenantRole RequiredRole(string method, string path)
    {
        // Tenant administration: the endpoints themselves require a platform admin.
        if (path.StartsWith("/api/v1/tenants", StringComparison.OrdinalIgnoreCase)) return TenantRole.Viewer;
        // The tenant's own keys and members: admins may look, owners may change.
        if (path.StartsWith("/api/v1/tenant/", StringComparison.OrdinalIgnoreCase))
            return HttpMethodsSafe(method) ? TenantRole.Admin : TenantRole.Owner;
        if (HttpMethodsSafe(method)) return TenantRole.Viewer;
        // Working with chats, tasks, approvals and memory is operator work.
        string[] operatorPrefixes =
        [
            "/api/v1/threads", "/api/v1/tasks", "/api/v1/approvals", "/api/v1/memory", "/api/v1/webchat", "/a2a",
        ];
        foreach (var p in operatorPrefixes)
            if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return TenantRole.Operator;
        if (path.StartsWith("/api/v1/schedules/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/run", StringComparison.OrdinalIgnoreCase))
            return TenantRole.Operator;
        return TenantRole.Admin;
    }

    private static bool HttpMethodsSafe(string m) => m is "GET" or "HEAD" or "OPTIONS";

    public static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static TenantRole? ParseRole(string? s) => Enum.TryParse<TenantRole>(s, true, out var r) && Enum.IsDefined(r) ? r : null;
}

/// <summary>
/// Tenant registry, tenant API keys and OIDC memberships. Lives in the shared database under the reserved tenant
/// "_platform" (SQLite: the root data directory's marbots.db).
/// </summary>
public sealed class PlatformStore
{
    private readonly DocumentStore<TenantRecord> _tenants;
    private readonly DocumentStore<TenantApiKey> _keys;
    private readonly DocumentStore<TenantMember> _members;
    private readonly ConcurrentDictionary<string, (TenantApiKey Key, DateTimeOffset At)> _keyCache = new();
    private volatile IReadOnlyList<TenantRecord>? _tenantCache;

    public PlatformStore(MarbotsOptions options)
    {
        var db = MarbotsDatabase.Create(options.Database, options.DataPath("marbots.db"), Tenants.Platform);
        var ctx = MarbotsJsonContext.Default;
        _tenants = new(db, "tenant", ctx.TenantRecord, t => t.Id);
        _keys = new(db, "apikey", ctx.TenantApiKey, k => k.Id);
        _members = new(db, "member", ctx.TenantMember, m => m.Id);
    }

    public async Task EnsureDefaultAsync(CancellationToken ct = default)
    {
        if (await _tenants.GetAsync(Tenants.Default, ct) is null)
            await _tenants.UpsertAsync(new TenantRecord { Id = Tenants.Default, Name = "Default" }, ct);
        _tenantCache = null;
    }

    public async Task<IReadOnlyList<TenantRecord>> ListTenantsAsync(CancellationToken ct = default) =>
        _tenantCache ??= [.. (await _tenants.ListAsync(ct)).OrderBy(t => t.Id == Tenants.Default ? "" : t.Id, StringComparer.Ordinal)];

    public async Task<TenantRecord?> GetTenantAsync(string id, CancellationToken ct = default) =>
        (await ListTenantsAsync(ct)).FirstOrDefault(t => t.Id == id);

    public async Task<TenantRecord> CreateTenantAsync(string id, string? name, CancellationToken ct = default)
    {
        id = id.Trim().ToLowerInvariant();
        if (!Tenants.IsValidId(id)) throw new ArgumentException("Tenant id must be 2-40 characters: a-z, 0-9 and '-', starting with a letter or digit.");
        if (await _tenants.GetAsync(id, ct) is not null) throw new InvalidOperationException($"Tenant '{id}' already exists.");
        var t = new TenantRecord { Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim() };
        await _tenants.UpsertAsync(t, ct);
        _tenantCache = null;
        return t;
    }

    public async Task<TenantRecord?> SetDisabledAsync(string id, bool disabled, CancellationToken ct = default)
    {
        if (id == Tenants.Default && disabled) throw new InvalidOperationException("The default tenant cannot be disabled.");
        var t = await _tenants.GetAsync(id, ct);
        if (t is null) return null;
        t.Disabled = disabled;
        await _tenants.UpsertAsync(t, ct);
        _tenantCache = null;
        _keyCache.Clear();
        return t;
    }

    public async Task<CreateApiKeyResult> CreateKeyAsync(string tenant, string name, TenantRole role, CancellationToken ct = default)
    {
        var key = $"mbk_{tenant}_{Base64Url(RandomNumberGenerator.GetBytes(24))}";
        var record = new TenantApiKey
        {
            Id = Tenants.HashKey(key), Tenant = tenant, Name = string.IsNullOrWhiteSpace(name) ? "key" : name.Trim(), Role = role,
            Prefix = key[..Math.Min(key.Length, tenant.Length + 9)],
        };
        await _keys.UpsertAsync(record, ct);
        return new CreateApiKeyResult(record.Id, key, tenant, role);
    }

    /// <summary>The key's record when it is valid and its tenant is enabled.</summary>
    public async Task<TenantApiKey?> ValidateKeyAsync(string key, CancellationToken ct = default)
    {
        if (!key.StartsWith("mbk_", StringComparison.Ordinal)) return null;
        var id = Tenants.HashKey(key);
        var now = DateTimeOffset.UtcNow;
        if (_keyCache.TryGetValue(id, out var hit) && now - hit.At < TimeSpan.FromSeconds(30)) return hit.Key;
        var record = await _keys.GetAsync(id, ct);
        if (record is null || await GetTenantAsync(record.Tenant, ct) is not { Disabled: false })
        {
            _keyCache.TryRemove(id, out _);
            return null;
        }
        if (record.LastUsedAt is null || now - record.LastUsedAt > TimeSpan.FromMinutes(5))
        {
            record.LastUsedAt = now;
            await _keys.UpsertAsync(record, ct);
        }
        _keyCache[id] = (record, now);
        return record;
    }

    public async Task<IReadOnlyList<TenantApiKey>> ListKeysAsync(string tenant, CancellationToken ct = default) =>
        [.. (await _keys.ListAsync(ct)).Where(k => k.Tenant == tenant).OrderBy(k => k.CreatedAt)];

    public async Task<bool> RevokeKeyAsync(string tenant, string id, CancellationToken ct = default)
    {
        var k = await _keys.GetAsync(id, ct);
        if (k is null || k.Tenant != tenant) return false;
        _keyCache.TryRemove(id, out _);
        return await _keys.DeleteAsync(id, ct);
    }

    public async Task<TenantMember> SetMemberAsync(string tenant, string subject, TenantRole role, CancellationToken ct = default)
    {
        subject = subject.Trim().ToLowerInvariant();
        if (subject.Length == 0) throw new ArgumentException("Subject (e-mail or OIDC subject) is required.");
        var m = new TenantMember { Id = $"{tenant}:{subject}", Tenant = tenant, Subject = subject, Role = role };
        await _members.UpsertAsync(m, ct);
        return m;
    }

    public async Task<IReadOnlyList<TenantMember>> ListMembersAsync(string tenant, CancellationToken ct = default) =>
        [.. (await _members.ListAsync(ct)).Where(m => m.Tenant == tenant).OrderBy(m => m.Subject, StringComparer.Ordinal)];

    public Task<bool> RemoveMemberAsync(string tenant, string subject, CancellationToken ct = default) =>
        _members.DeleteAsync($"{tenant}:{subject.Trim().ToLowerInvariant()}", ct);

    /// <summary>Memberships of a user, matched on any of their identifiers (e-mail, subject).</summary>
    public async Task<IReadOnlyList<TenantMember>> MembershipsAsync(IEnumerable<string?> subjects, CancellationToken ct = default)
    {
        var set = subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0) return [];
        var enabled = (await ListTenantsAsync(ct)).Where(t => !t.Disabled).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return [.. (await _members.ListAsync(ct)).Where(m => set.Contains(m.Subject) && enabled.Contains(m.Tenant))];
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// One isolated runtime (its own service provider: engine, stores, scheduler, channels, hosts…) per tenant.
/// In single-tenant mode the root provider is the only runtime.
/// </summary>
public sealed class TenantRuntimeManager(MarbotsOptions rootOptions, IServiceProvider root, PlatformStore platform, ILogger<TenantRuntimeManager> log)
    : IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ServiceProvider>>> _runtimes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _stopLock = new(1, 1);

    public bool MultiTenant => rootOptions.MultiTenant;
    public PlatformStore Platform => platform;
    public IReadOnlyCollection<string> Running => [.. _runtimes.Where(r => r.Value.IsValueCreated && r.Value.Value.IsCompletedSuccessfully).Select(r => r.Key)];

    /// <summary>The tenant's runtime, starting it on first use.</summary>
    public async ValueTask<IServiceProvider> GetAsync(string tenant, CancellationToken ct = default)
    {
        if (!MultiTenant) return root;
        var lazy = _runtimes.GetOrAdd(tenant, t => new Lazy<Task<ServiceProvider>>(() => StartRuntimeAsync(t)));
        try
        {
            return await lazy.Value.WaitAsync(ct);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _runtimes.TryRemove(new(tenant, lazy));
            throw;
        }
    }

    /// <summary>Synchronous access for DI forwarding; the request pipeline warms the runtime first, so this rarely blocks.</summary>
    public IServiceProvider Get(string tenant)
    {
        if (!MultiTenant) return root;
        if (_runtimes.TryGetValue(tenant, out var l) && l.IsValueCreated && l.Value.IsCompletedSuccessfully) return l.Value.Result;
        return GetAsync(tenant).AsTask().GetAwaiter().GetResult();
    }

    public static MarbotsOptions OptionsFor(MarbotsOptions root, string tenant)
    {
        var o = root.Clone();
        o.TenantId = tenant;
        if (tenant != Tenants.Default) o.DataDirectory = root.DataPath("tenants", tenant);
        return o;
    }

    private async Task<ServiceProvider> StartRuntimeAsync(string tenant)
    {
        if (await platform.GetTenantAsync(tenant) is not { Disabled: false })
            throw new UnknownTenantException(tenant);
        var options = OptionsFor(rootOptions, tenant);
        Directory.CreateDirectory(options.DataDirectory);
        var services = new ServiceCollection();
        services.AddSingleton(root.GetRequiredService<ILoggerFactory>());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(root.GetRequiredService<IConfiguration>());
        services.AddSingleton(root.GetRequiredService<IDataProtectionProvider>());
        services.AddSingleton(root.GetRequiredService<IHostApplicationLifetime>());
        services.AddMarbotsRuntime(options);
        var sp = services.BuildServiceProvider();
        foreach (var h in sp.GetServices<IHostedService>()) await h.StartAsync(CancellationToken.None);
        log.LogInformation("Tenant runtime {Tenant} started ({Data})", tenant, options.DataDirectory);
        return sp;
    }

    /// <summary>Stops a tenant's runtime (e.g. after disabling it). It starts again on next use if still enabled.</summary>
    public async Task StopTenantAsync(string tenant, CancellationToken ct = default)
    {
        if (!_runtimes.TryRemove(tenant, out var lazy) || !lazy.IsValueCreated) return;
        ServiceProvider sp;
        try { sp = await lazy.Value; }
        catch (Exception) { return; }
        await StopProviderAsync(sp, ct);
    }

    private static async Task StopProviderAsync(ServiceProvider sp, CancellationToken ct)
    {
        foreach (var h in sp.GetServices<IHostedService>().Reverse())
        {
            try { await h.StopAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { }
        }
        await sp.DisposeAsync();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await platform.EnsureDefaultAsync(cancellationToken);
        if (!MultiTenant) return;
        foreach (var t in await platform.ListTenantsAsync(cancellationToken))
        {
            if (t.Disabled) continue;
            try { await GetAsync(t.Id, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Tenant runtime {Tenant} failed to start", t.Id); }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var t in _runtimes.Keys.ToList()) await StopTenantAsync(t, cancellationToken);
        }
        finally { _stopLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None); }
        catch (ObjectDisposedException) { }
    }
}

public sealed class UnknownTenantException(string tenant) : Exception($"Unknown or disabled tenant '{tenant}'.")
{
    public string Tenant { get; } = tenant;
}

/// <summary>
/// The tenant of the current request or Blazor circuit. Runtime services injected in multi-tenant mode are forwarded
/// to this tenant's provider.
/// </summary>
public sealed class TenantAccessor(TenantRuntimeManager manager)
{
    private IServiceProvider? _services;

    public string TenantId { get; private set; } = Tenants.Default;
    public TenantRole Role { get; private set; } = TenantRole.Owner;
    public string? User { get; private set; }
    public bool PlatformAdmin { get; private set; }
    public bool IsResolved { get; private set; }

    public IServiceProvider Services
    {
        get
        {
            if (_services is not null) return _services;
            if (!IsResolved && manager.MultiTenant) throw new InvalidOperationException("No tenant has been resolved for this request.");
            return _services = manager.Get(TenantId);
        }
    }

    public async ValueTask SetAsync(string tenant, TenantRole role, string? user, bool platformAdmin, CancellationToken ct = default)
    {
        TenantId = tenant;
        Role = role;
        User = user;
        PlatformAdmin = platformAdmin;
        _services = await manager.GetAsync(tenant, ct);
        IsResolved = true;
    }

    public bool Can(TenantRole role) => Role >= role;
}

public static class MultiTenantServiceCollectionExtensions
{
    /// <summary>
    /// Registers tenancy (platform store, runtime manager, accessor). In multi-tenant mode every runtime service type is
    /// registered as a scoped forwarder to the current tenant's runtime; otherwise the runtime is registered directly.
    /// </summary>
    public static IServiceCollection AddMarbotsTenancy(this IServiceCollection services, MarbotsOptions options)
    {
        services.AddSingleton<PlatformStore>(_ => new PlatformStore(options));
        services.AddSingleton(sp => new TenantRuntimeManager(options, sp, sp.GetRequiredService<PlatformStore>(), sp.GetRequiredService<ILogger<TenantRuntimeManager>>()));
        services.AddHostedService(sp => sp.GetRequiredService<TenantRuntimeManager>());
        services.AddScoped<TenantAccessor>();
        if (!options.MultiTenant) return services.AddMarbotsRuntime(options);

        foreach (var type in ForwardedTypes(options))
            services.AddScoped(type, sp => sp.GetRequiredService<TenantAccessor>().Services.GetRequiredService(type));
        services.AddHttpClient();
        return services;
    }

    /// <summary>
    /// Runtime service types forwarded to the tenant's provider. Their instances must not be IDisposable or
    /// IAsyncDisposable: the request/circuit scope that resolved them would dispose the tenant's singleton.
    /// </summary>
    public static IReadOnlyList<Type> ForwardedTypes(MarbotsOptions options)
    {
        var probe = new ServiceCollection();
        probe.AddMarbotsRuntime(options);
        return [.. probe
            .Where(d => d.ServiceType != typeof(IHostedService) && !d.ServiceType.IsGenericTypeDefinition &&
                        (d.ServiceType.Assembly.GetName().Name ?? "").StartsWith("Marbots.", StringComparison.Ordinal))
            .GroupBy(d => d.ServiceType)
            .Where(g => g.Count() == 1)
            .Select(g => g.Key)];
    }
}
