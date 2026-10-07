namespace Marbots.Abstractions;

/// <summary>Roles inside a tenant, in increasing order of power.</summary>
public enum TenantRole
{
    /// <summary>Read everything: chats, tasks, events, usage.</summary>
    Viewer = 0,
    /// <summary>Viewer + chat, run and cancel tasks, answer approvals, write memory.</summary>
    Operator = 1,
    /// <summary>Operator + manage bots, templates, skills, MCP, channels, hosts, schedules, models and settings.</summary>
    Admin = 2,
    /// <summary>Admin + manage the tenant's API keys and members.</summary>
    Owner = 3,
}

/// <summary>A tenant (organisation / workspace) in multi-tenant mode. "default" always exists.</summary>
public sealed class TenantRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Disabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A tenant API key. Only the SHA-256 hash is stored; the id is the hash.</summary>
public sealed class TenantApiKey
{
    public string Id { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Name { get; set; } = "";
    public TenantRole Role { get; set; } = TenantRole.Operator;
    /// <summary>First characters of the key, for recognising it in lists.</summary>
    public string Prefix { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>An OIDC user's membership of a tenant, matched by e-mail or subject claim.</summary>
public sealed class TenantMember
{
    /// <summary>"tenant:subject"</summary>
    public string Id { get; set; } = "";
    public string Tenant { get; set; } = "";
    /// <summary>Lower-cased e-mail address or OIDC subject.</summary>
    public string Subject { get; set; } = "";
    public TenantRole Role { get; set; } = TenantRole.Viewer;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record CreateTenantRequest(string Id, string? Name);
public sealed record CreateApiKeyRequest(string Name, TenantRole Role = TenantRole.Operator);
/// <summary>The plaintext key is returned once, at creation.</summary>
public sealed record CreateApiKeyResult(string Id, string Key, string Tenant, TenantRole Role);
public sealed record SetMemberRequest(string Subject, TenantRole Role);
/// <summary>Who the caller is: tenant, role and whether they can manage tenants.</summary>
public sealed record WhoAmI(string Tenant, TenantRole Role, string? User, bool PlatformAdmin, bool MultiTenant, IReadOnlyList<string> Tenants);
