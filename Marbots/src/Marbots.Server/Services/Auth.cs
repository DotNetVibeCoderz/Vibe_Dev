using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Marbots.Abstractions;
using Marbots.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Marbots.Server.Services;

/// <summary>Claims Marbots puts on every signed-in principal.</summary>
public static class MarbotsClaims
{
    public const string Tenant = "mb:tenant";
    public const string Role = "mb:role";
    public const string Platform = "mb:platform";
    public const string User = "mb:user";
    public const string Via = "mb:via";

    public const string LocalScheme = "Local";
    public const string TenantCookie = "mb-tenant";

    public static ClaimsIdentity Identity(string scheme, string tenant, TenantRole role, string? user, bool platform, string via, IEnumerable<Claim>? extra = null)
    {
        var id = new ClaimsIdentity(scheme, User, Role);
        id.AddClaim(new Claim(Tenant, tenant));
        id.AddClaim(new Claim(Role, role.ToString()));
        id.AddClaim(new Claim(Via, via));
        if (user is not null) id.AddClaim(new Claim(User, user));
        if (platform) id.AddClaim(new Claim(Platform, "true"));
        if (extra is not null) id.AddClaims(extra);
        return id;
    }

    public static (string Tenant, TenantRole Role, string? User, bool Platform)? Read(ClaimsPrincipal? p)
    {
        if (p?.Identity?.IsAuthenticated != true || p.FindFirst(Tenant)?.Value is not { Length: > 0 } t) return null;
        return (t, Tenants.ParseRole(p.FindFirst(Role)?.Value) ?? TenantRole.Viewer, p.FindFirst(User)?.Value, p.HasClaim(Platform, "true"));
    }

    public static bool HasRole(ClaimsPrincipal p, TenantRole role) => Read(p) is { } r && r.Role >= role;
}

/// <summary>Effective auth settings derived from configuration.</summary>
public sealed class AuthSettings(MarbotsOptions options, IConfiguration config)
{
    public MarbotsAuthOptions Auth => options.Auth;
    public bool MultiTenant => options.MultiTenant;
    public bool Oidc => string.Equals(Auth.Mode, "oidc", StringComparison.OrdinalIgnoreCase);
    public bool RequireUiLogin => Auth.RequireUiLogin ?? (options.MultiTenant || Oidc);
    public bool Bearer => Oidc || !string.IsNullOrEmpty(Auth.JwtSigningKey);
    /// <summary>The global (platform) API key; read live so tests and reloads can change it.</summary>
    public string? GlobalKey => config["Marbots:ApiKey"] is { Length: > 0 } k ? k : null;
    /// <summary>Single-tenant without a global key and without OIDC: the API is open, as before multi-tenancy.</summary>
    public bool ApiOpen => GlobalKey is null && !MultiTenant && !Oidc;
}

public static class AuthSetup
{
    public static IServiceCollection AddMarbotsAuth(this IServiceCollection services, MarbotsOptions options)
    {
        services.AddSingleton(sp => new AuthSettings(options, sp.GetRequiredService<IConfiguration>()));
        var oidc = string.Equals(options.Auth.Mode, "oidc", StringComparison.OrdinalIgnoreCase);
        var requireLogin = options.Auth.RequireUiLogin ?? (options.MultiTenant || oidc);
        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = requireLogin ? CookieAuthenticationDefaults.AuthenticationScheme : MarbotsClaims.LocalScheme;
            if (oidc && requireLogin) o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        });
        auth.AddScheme<AuthenticationSchemeOptions, LocalAuthHandler>(MarbotsClaims.LocalScheme, null);
        auth.AddCookie(o =>
        {
            o.Cookie.Name = "marbots.auth";
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.HttpOnly = true;
            o.LoginPath = "/login";
            o.LogoutPath = "/logout";
            o.AccessDeniedPath = "/login";
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.SlidingExpiration = true;
            // API calls get a status code, not a redirect to the login page.
            o.Events.OnRedirectToLogin = ctx => ApiAware(ctx, 401);
            o.Events.OnRedirectToAccessDenied = ctx => ApiAware(ctx, 403);
        });
        var a = options.Auth;
        if (oidc)
        {
            if (string.IsNullOrWhiteSpace(a.Authority) || string.IsNullOrWhiteSpace(a.ClientId))
                throw new InvalidOperationException("Marbots:Auth:Authority and Marbots:Auth:ClientId are required when Marbots:Auth:Mode is 'oidc'.");
            auth.AddOpenIdConnect(o =>
            {
                o.Authority = a.Authority;
                o.ClientId = a.ClientId;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.SaveTokens = false;
                o.MapInboundClaims = false;
                o.Scope.Clear();
                foreach (var s in new[] { "openid", "profile", "email" }) o.Scope.Add(s);
                o.Events.OnTokenValidated = async ctx =>
                {
                    var platform = ctx.HttpContext.RequestServices.GetRequiredService<PlatformStore>();
                    var mapped = await MapExternalAsync(ctx.Principal!, platform, a, ctx.HttpContext.Request.Cookies[MarbotsClaims.TenantCookie], "oidc", ctx.HttpContext.RequestAborted);
                    if (mapped is null) ctx.Fail("You are not a member of any Marbots tenant.");
                    else ctx.Principal = mapped;
                };
            });
            services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme).Configure<IConfiguration>((o, config) =>
            {
                if (a.ClientSecret is { Length: > 0 } name)
                    o.ClientSecret = config[$"Marbots:Secrets:{name}"] is { Length: > 0 } v ? v : Environment.GetEnvironmentVariable(name) ?? name;
            });
        }
        if (oidc || !string.IsNullOrEmpty(a.JwtSigningKey))
        {
            auth.AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                if (oidc) o.Authority = a.Authority;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudiences = new[] { a.Audience, a.ClientId }.Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToArray(),
                    ValidateIssuer = !string.IsNullOrEmpty(a.JwtIssuer) || oidc,
                    ValidIssuer = a.JwtIssuer,
                    NameClaimType = "email",
                };
                if (!string.IsNullOrEmpty(a.JwtSigningKey))
                {
                    if (a.JwtSigningKey.Length < 32) throw new InvalidOperationException("Marbots:Auth:JwtSigningKey must be at least 32 characters.");
                    o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(a.JwtSigningKey));
                    if (!oidc) o.TokenValidationParameters.ValidateIssuer = !string.IsNullOrEmpty(a.JwtIssuer);
                }
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var platform = ctx.HttpContext.RequestServices.GetRequiredService<PlatformStore>();
                        var requested = ctx.HttpContext.Items[TenantPathMiddleware.ItemKey] as string ?? ctx.HttpContext.Request.Headers[TenantMiddleware.TenantHeader].ToString();
                        var mapped = await MapExternalAsync(ctx.Principal!, platform, a, requested, "jwt", ctx.HttpContext.RequestAborted);
                        if (mapped is null) ctx.Fail("Token has no Marbots tenant membership.");
                        else ctx.Principal = mapped;
                    },
                };
            });
        }

        services.AddAuthorizationBuilder()
            .AddPolicy("Operator", p => p.RequireAssertion(c => MarbotsClaims.HasRole(c.User, TenantRole.Operator)))
            .AddPolicy("Admin", p => p.RequireAssertion(c => MarbotsClaims.HasRole(c.User, TenantRole.Admin)))
            .AddPolicy("Owner", p => p.RequireAssertion(c => MarbotsClaims.HasRole(c.User, TenantRole.Owner)));
        services.AddCascadingAuthenticationState();
        services.AddScoped<CircuitHandler, TenantCircuitHandler>();
        return services;
    }

    private static Task ApiAware(RedirectContext<CookieAuthenticationOptions> ctx, int status)
    {
        var p = ctx.Request.Path;
        if (p.StartsWithSegments("/api") || p.StartsWithSegments("/a2a") || ctx.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = status;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps an external identity (OIDC / JWT) to a Marbots principal: tenant from the requested tenant, the tenant claim
    /// or the first membership; role from the membership, the role claim, or Owner for platform admins.
    /// </summary>
    public static async Task<ClaimsPrincipal?> MapExternalAsync(ClaimsPrincipal external, PlatformStore platform, MarbotsAuthOptions auth, string? requested, string via, CancellationToken ct)
    {
        string? Claim(params string[] types) => types.Select(t => external.FindFirst(t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var email = Claim("email", ClaimTypes.Email, "preferred_username", "upn");
        var sub = Claim("sub", ClaimTypes.NameIdentifier, "oid");
        var ids = new[] { email, sub }.Where(x => x is not null).Select(x => x!.ToLowerInvariant()).ToList();
        var isPlatform = auth.PlatformAdmins.Any(p => ids.Contains(p.Trim().ToLowerInvariant()));
        var memberships = await platform.MembershipsAsync(ids, ct);
        var claimTenant = Claim(auth.TenantClaim);
        var claimRole = external.FindAll(auth.RoleClaim).Select(c => Tenants.ParseRole(c.Value)).Where(r => r is not null).Max();

        async Task<bool> Enabled(string? t) => t is not null && await platform.GetTenantAsync(t, ct) is { Disabled: false };
        TenantRole? RoleIn(string t) =>
            memberships.FirstOrDefault(m => m.Tenant == t)?.Role ?? (t == claimTenant ? claimRole ?? TenantRole.Viewer : (TenantRole?)null) ?? (isPlatform ? TenantRole.Owner : null);

        string? tenant = null;
        foreach (var candidate in new[] { requested, claimTenant, memberships.FirstOrDefault()?.Tenant, isPlatform ? Tenants.Default : null })
        {
            if (string.IsNullOrWhiteSpace(candidate) || RoleIn(candidate) is null || !await Enabled(candidate)) continue;
            tenant = candidate;
            break;
        }
        if (tenant is null) return null;
        var role = isPlatform ? TenantRole.Owner : RoleIn(tenant)!.Value;
        var extra = new[] { new Claim("email", email ?? ""), new Claim("sub", sub ?? "") };
        return new ClaimsPrincipal(MarbotsClaims.Identity(external.Identity?.AuthenticationType ?? via, tenant, role, email ?? sub, isPlatform, via, extra));
    }

    /// <summary>Tenants the principal may switch to.</summary>
    public static async Task<IReadOnlyList<string>> AccessibleTenantsAsync(ClaimsPrincipal p, PlatformStore platform, CancellationToken ct)
    {
        if (MarbotsClaims.Read(p) is not { } who) return [];
        if (who.Platform) return [.. (await platform.ListTenantsAsync(ct)).Where(t => !t.Disabled).Select(t => t.Id)];
        if (p.FindFirst(MarbotsClaims.Via)?.Value is "oidc" or "jwt")
            return [.. (await platform.MembershipsAsync([p.FindFirst("email")?.Value, p.FindFirst("sub")?.Value], ct)).Select(m => m.Tenant).Distinct()];
        return [who.Tenant];
    }
}

/// <summary>Used when the UI does not require sign-in (single-tenant, local console): the visitor is the owner.</summary>
public sealed class LocalAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PlatformStore platform)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var tenant = Tenants.Default;
        if (Request.Cookies[MarbotsClaims.TenantCookie] is { Length: > 0 } t && t != tenant && await platform.GetTenantAsync(t, Context.RequestAborted) is { Disabled: false })
            tenant = t;
        var id = MarbotsClaims.Identity(Scheme.Name, tenant, TenantRole.Owner, "local", platform: true, via: "local");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name));
    }
}

/// <summary>Turns <c>/t/{tenant}/api/…</c> into <c>/api/…</c> with the tenant pinned (webhooks, agent hosts, SDK base URLs).</summary>
public sealed class TenantPathMiddleware(RequestDelegate next)
{
    public const string ItemKey = "mb:path-tenant";

    public Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/t", out var rest) && rest.Value is { Length: > 1 } r)
        {
            var slash = r.IndexOf('/', 1);
            var tenant = slash < 0 ? r[1..] : r[1..slash];
            if (Tenants.IsValidId(tenant))
            {
                ctx.Items[ItemKey] = tenant;
                ctx.Request.PathBase = ctx.Request.PathBase.Add("/t/" + tenant);
                ctx.Request.Path = slash < 0 ? "/" : r[slash..];
            }
        }
        return next(ctx);
    }
}

/// <summary>
/// Resolves who is calling and which tenant they act in, enforces roles on the API, and points the request's
/// <see cref="TenantAccessor"/> at that tenant's runtime.
/// </summary>
public sealed class TenantMiddleware(RequestDelegate next, AuthSettings settings, PlatformStore platform, ILogger<TenantMiddleware> log)
{
    public const string TenantHeader = "X-Marbots-Tenant";

    /// <summary>Webhook, channel and agent-host endpoints authenticate with their own secret or signature.</summary>
    public static bool IsSelfAuthenticated(PathString path)
    {
        var p = path.Value ?? "";
        if (p.StartsWith("/api/v1/hooks/", StringComparison.Ordinal)) return true;
        if (p is "/api/v1/hosts/enroll" or "/api/v1/hosts/connect") return true;
        return p.StartsWith("/api/v1/channels/", StringComparison.Ordinal) &&
               (p.EndsWith("/inbound", StringComparison.Ordinal) || p.EndsWith("/slack", StringComparison.Ordinal) ||
                p.EndsWith("/whatsapp", StringComparison.Ordinal) || p.EndsWith("/telegram", StringComparison.Ordinal));
    }

    public async Task InvokeAsync(HttpContext ctx, TenantAccessor accessor)
    {
        var path = ctx.Request.Path;
        var isApi = path.StartsWithSegments("/api") || path.StartsWithSegments("/a2a");
        var pathTenant = ctx.Items[TenantPathMiddleware.ItemKey] as string;
        var headerTenant = ctx.Request.Headers[TenantHeader].ToString() is { Length: > 0 } h ? h : null;
        var requested = pathTenant ?? headerTenant;
        try
        {
            if (!isApi)
            {
                if (MarbotsClaims.Read(ctx.User) is { } u) await accessor.SetAsync(u.Tenant, u.Role, u.User, u.Platform, ctx.RequestAborted);
                else await accessor.SetAsync(requested ?? Tenants.Default, TenantRole.Viewer, null, false, ctx.RequestAborted);
                await next(ctx);
                return;
            }
            if (IsSelfAuthenticated(path))
            {
                await accessor.SetAsync(requested ?? Tenants.Default, TenantRole.Operator, null, false, ctx.RequestAborted);
                await next(ctx);
                return;
            }
            var who = await AuthenticateApiAsync(ctx, requested);
            if (who is null)
            {
                await Problem(ctx, 401, "Missing or invalid API key or token");
                return;
            }
            var (tenant, role, user, isPlatform) = who.Value;
            if (requested is not null && requested != tenant)
            {
                await Problem(ctx, 403, $"This credential is not valid for tenant '{requested}'");
                return;
            }
            var required = Tenants.RequiredRole(ctx.Request.Method, path.Value ?? "");
            if (role < required)
            {
                await Problem(ctx, 403, $"Requires the {required} role (you are {role})");
                return;
            }
            await accessor.SetAsync(tenant, role, user, isPlatform, ctx.RequestAborted);
        }
        catch (UnknownTenantException ex)
        {
            if (isApi) await Problem(ctx, 404, ex.Message);
            else ctx.Response.Redirect("/logout");
            return;
        }
        await next(ctx);
    }

    private async Task<(string Tenant, TenantRole Role, string? User, bool Platform)?> AuthenticateApiAsync(HttpContext ctx, string? requested)
    {
        var supplied = ctx.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrEmpty(supplied) && ctx.Request.Headers.Authorization.ToString() is { } auth && auth.StartsWith("Bearer ", StringComparison.Ordinal))
            supplied = auth[7..].Trim();

        if (supplied.StartsWith("mbk_", StringComparison.Ordinal))
            return await platform.ValidateKeyAsync(supplied, ctx.RequestAborted) is { } k ? (k.Tenant, k.Role, "key:" + k.Name, false) : null;
        if (settings.GlobalKey is { } global && supplied.Length > 0 &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(global)))
            return (requested ?? Tenants.Default, TenantRole.Owner, "platform-key", true);
        if (settings.Bearer && supplied.Count(c => c == '.') == 2)
        {
            var result = await ctx.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
            if (result.Succeeded && MarbotsClaims.Read(result.Principal) is { } j)
            {
                ctx.User = result.Principal;
                return j;
            }
            log.LogDebug("Bearer token rejected: {Error}", result.Failure?.Message);
            return null;
        }
        if (supplied.Length > 0) return null;
        // Browser calls from the signed-in UI (file downloads, exports) carry the auth cookie.
        if (MarbotsClaims.Read(ctx.User) is { } cookie && ctx.User.Identity?.AuthenticationType != MarbotsClaims.LocalScheme) return cookie;
        if (settings.ApiOpen) return (Tenants.Default, TenantRole.Owner, null, true);
        // The local console (no UI sign-in) may call its own API from the browser when the API is open; otherwise a key is needed.
        return null;
    }

    private static Task Problem(HttpContext ctx, int status, string title)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new ProblemDetails { Title = title, Status = status });
    }
}

/// <summary>Points a Blazor circuit's <see cref="TenantAccessor"/> at the signed-in user's tenant before any page renders.</summary>
public sealed class TenantCircuitHandler(TenantAccessor accessor, AuthenticationStateProvider auth) : CircuitHandler
{
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var state = await auth.GetAuthenticationStateAsync();
        if (MarbotsClaims.Read(state.User) is { } u) await accessor.SetAsync(u.Tenant, u.Role, u.User, u.Platform, cancellationToken);
    }
}
