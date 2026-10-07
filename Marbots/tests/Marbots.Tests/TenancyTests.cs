using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Marbots.Abstractions;
using Marbots.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Marbots.Tests;

/// <summary>Multi-tenant mode end to end: platform key, tenant keys and roles, JWT members, path prefixes and UI sign-in.</summary>
public sealed class TenancyTests : IClassFixture<TenancyTests.Fixture>
{
    public const string PlatformKey = "platform-test-key-0123456789";
    public const string JwtKey = "jwt-signing-key-for-tests-0123456789abcdef";

    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mb-tenancy-" + Guid.NewGuid().ToString("N")[..8]);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Marbots:DataDirectory", DataDir);
            builder.UseSetting("Marbots:MultiTenant", "true");
            builder.UseSetting("Marbots:ApiKey", PlatformKey);
            builder.UseSetting("Marbots:Auth:JwtSigningKey", JwtKey);
            builder.UseSetting("Marbots:Auth:Audience", "marbots");
            builder.UseSetting("Marbots:Auth:PlatformAdmins:0", "root@example.com");
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Fixture _fx;
    public TenancyTests(Fixture fx) => _fx = fx;

    private HttpClient Client(string? key, string? tenant = null)
    {
        var c = _fx.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (key is not null) c.DefaultRequestHeaders.Add("X-Api-Key", key);
        if (tenant is not null) c.DefaultRequestHeaders.Add("X-Marbots-Tenant", tenant);
        return c;
    }

    private async Task<string> NewTenantAsync()
    {
        var id = "t" + Guid.NewGuid().ToString("N")[..8];
        var r = await Client(PlatformKey).PostAsJsonAsync("/api/v1/tenants", new CreateTenantRequest(id, "Test " + id));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return id;
    }

    private async Task<string> KeyAsync(string tenant, TenantRole role)
    {
        var r = await Client(PlatformKey).PostAsJsonAsync($"/api/v1/tenants/{tenant}/keys", new CreateApiKeyRequest(role.ToString(), role));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<CreateApiKeyResult>(Json))!.Key;
    }

    [Fact]
    public async Task Tenants_have_isolated_runtimes_and_data()
    {
        var acme = await NewTenantAsync();
        var owner = await KeyAsync(acme, TenantRole.Owner);
        var created = await Client(owner).PostAsJsonAsync("/api/v1/bots", new BotDefinition { Id = "acme-only", Name = "Acme Only", Persona = "x" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var acmeBots = await Client(owner).GetFromJsonAsync<List<BotDefinition>>("/api/v1/bots", Json);
        Assert.Contains(acmeBots!, b => b.Id == "acme-only");
        Assert.Contains(acmeBots!, b => b.Id == WellKnown.BossManId);
        var defaultBots = await Client(PlatformKey).GetFromJsonAsync<List<BotDefinition>>("/api/v1/bots", Json);
        Assert.DoesNotContain(defaultBots!, b => b.Id == "acme-only");
        // The platform key acts in any tenant, by header or by /t/{tenant} prefix.
        Assert.Contains((await Client(PlatformKey, acme).GetFromJsonAsync<List<BotDefinition>>("/api/v1/bots", Json))!, b => b.Id == "acme-only");
        Assert.Contains((await Client(PlatformKey).GetFromJsonAsync<List<BotDefinition>>($"/t/{acme}/api/v1/bots", Json))!, b => b.Id == "acme-only");
        Assert.True(Directory.Exists(Path.Combine(_fx.DataDir, "tenants", acme)));

        // Chat twice in the tenant: the second request must find the tenant's stores intact.
        for (var i = 0; i < 2; i++)
        {
            var thread = await (await Client(owner).PostAsJsonAsync("/api/v1/threads", new { title = "t" + i })).Content.ReadFromJsonAsync<ChatThread>(Json);
            var sent = await Client(owner).PostAsJsonAsync($"/api/v1/threads/{thread!.Id}/messages", new { text = "hello " + i, wait = true });
            Assert.True(sent.StatusCode == HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await Client(PlatformKey).GetAsync($"/api/v1/threads/{thread.Id}")).StatusCode);
        }

        var who = await Client(owner).GetFromJsonAsync<WhoAmI>("/api/v1/whoami", Json);
        Assert.Equal(acme, who!.Tenant);
        Assert.Equal(TenantRole.Owner, who.Role);
        Assert.False(who.PlatformAdmin);
        Assert.True(who.MultiTenant);
    }

    [Fact]
    public async Task Roles_and_tenant_binding_are_enforced()
    {
        var acme = await NewTenantAsync();
        var viewer = await KeyAsync(acme, TenantRole.Viewer);
        var op = await KeyAsync(acme, TenantRole.Operator);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync("/api/v1/bots")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("mbk_" + acme + "_forged").GetAsync("/api/v1/bots")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(viewer).GetAsync("/api/v1/bots")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(viewer).PostAsJsonAsync("/api/v1/threads", new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(op).PostAsJsonAsync("/api/v1/bots", new BotDefinition { Id = "nope", Name = "Nope" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await Client(op).PostAsJsonAsync("/api/v1/threads", new { title = "ok" })).StatusCode);
        // A tenant key cannot reach another tenant.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(viewer, Tenants.Default).GetAsync("/api/v1/bots")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(viewer).GetAsync($"/t/{Tenants.Default}/api/v1/bots")).StatusCode);
        // Only platform admins manage tenants; owners manage their own keys.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(op).GetAsync("/api/v1/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(op).PostAsJsonAsync("/api/v1/tenant/keys", new CreateApiKeyRequest("x"))).StatusCode);
        var owner = await KeyAsync(acme, TenantRole.Owner);
        var made = await (await Client(owner).PostAsJsonAsync("/api/v1/tenant/keys", new CreateApiKeyRequest("ci", TenantRole.Operator))).Content.ReadFromJsonAsync<CreateApiKeyResult>(Json);
        Assert.StartsWith($"mbk_{acme}_", made!.Key);
        Assert.Contains((await Client(owner).GetFromJsonAsync<List<TenantApiKey>>("/api/v1/tenant/keys", Json))!, k => k.Id == made.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await Client(owner).DeleteAsync($"/api/v1/tenant/keys/{made.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(made.Key).GetAsync("/api/v1/bots")).StatusCode);

        // Disabling a tenant locks its keys out.
        Assert.Equal(HttpStatusCode.OK, (await Client(PlatformKey).PostAsync($"/api/v1/tenants/{acme}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(owner).GetAsync("/api/v1/bots")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client(PlatformKey, acme).GetAsync("/api/v1/bots")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client(PlatformKey).PostAsync($"/api/v1/tenants/{Tenants.Default}/disable", null)).StatusCode);
    }

    private static string Token(string email, string? tenantClaim = null)
    {
        var claims = new Dictionary<string, object> { ["email"] = email, ["sub"] = "sub-" + email };
        if (tenantClaim is not null) claims["tenant"] = tenantClaim;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Audience = "marbots",
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256),
        });
    }

    [Fact]
    public async Task Bearer_tokens_map_to_memberships()
    {
        var acme = await NewTenantAsync();
        var owner = await KeyAsync(acme, TenantRole.Owner);
        Assert.Equal(HttpStatusCode.OK, (await Client(owner).PutAsJsonAsync("/api/v1/tenant/members", new SetMemberRequest("Ana@Example.com", TenantRole.Operator))).StatusCode);

        var ana = Client(null);
        ana.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("ana@example.com"));
        var who = await ana.GetFromJsonAsync<WhoAmI>("/api/v1/whoami", Json);
        Assert.Equal((acme, TenantRole.Operator, false), (who!.Tenant, who.Role, who.PlatformAdmin));
        Assert.Equal(HttpStatusCode.Forbidden, (await ana.PostAsJsonAsync("/api/v1/bots", new BotDefinition { Id = "x", Name = "X" })).StatusCode);

        var stranger = Client(null);
        stranger.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("eve@example.com"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/v1/bots")).StatusCode);
        // A tenant claim alone grants Viewer in that tenant.
        var claimed = Client(null);
        claimed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("bob@example.com", acme));
        Assert.Equal(TenantRole.Viewer, (await claimed.GetFromJsonAsync<WhoAmI>("/api/v1/whoami", Json))!.Role);

        var root = Client(null, acme);
        root.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("root@example.com"));
        var rootWho = await root.GetFromJsonAsync<WhoAmI>("/api/v1/whoami", Json);
        Assert.True(rootWho!.PlatformAdmin);
        Assert.Equal(acme, rootWho.Tenant);
        Assert.Contains(acme, rootWho.Tenants);

        var forged = Client(null);
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("ana@example.com")[..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/v1/bots")).StatusCode);
    }

    [Fact]
    public async Task Web_ui_requires_sign_in_and_shows_the_tenant()
    {
        var acme = await NewTenantAsync();
        var key = await KeyAsync(acme, TenantRole.Admin);
        var c = _fx.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var anon = await c.GetAsync("/team");
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);
        Assert.StartsWith("/login", anon.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("API key", await c.GetStringAsync("/login"));

        var bad = await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["key"] = "wrong", ["returnUrl"] = "/team" }));
        Assert.Contains("error=1", bad.Headers.Location!.ToString());
        var ok = await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["key"] = key, ["returnUrl"] = "https://evil.example/" }));
        Assert.Equal("/", ok.Headers.Location!.ToString());
        var page = await c.GetAsync("/team");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains(acme, await page.Content.ReadAsStringAsync());
        // The signed-in cookie also works for the API (file downloads from the UI).
        Assert.Equal(acme, (await c.GetFromJsonAsync<WhoAmI>("/api/v1/whoami", Json))!.Tenant);
    }

    [Fact]
    public async Task Forwarded_tenant_services_are_never_disposable()
    {
        var acme = await NewTenantAsync();
        var runtime = await _fx.Services.GetRequiredService<TenantRuntimeManager>().GetAsync(acme);
        var options = runtime.GetRequiredService<MarbotsOptions>();
        Assert.Equal(acme, options.TenantId);
        var disposable = MultiTenantServiceCollectionExtensions.ForwardedTypes(options)
            .Select(t => (t, runtime.GetRequiredService(t)))
            .Where(x => x.Item2 is IDisposable or IAsyncDisposable)
            .Select(x => x.t.Name + " → " + x.Item2.GetType().Name)
            .ToList();
        Assert.True(disposable.Count == 0, string.Join(", ", disposable));
    }

    [Fact]
    public void Required_roles_follow_the_route_table()
    {
        Assert.Equal(TenantRole.Viewer, Tenants.RequiredRole("GET", "/api/v1/bots"));
        Assert.Equal(TenantRole.Operator, Tenants.RequiredRole("POST", "/api/v1/threads/abc/messages"));
        Assert.Equal(TenantRole.Operator, Tenants.RequiredRole("POST", "/api/v1/approvals/x/approve"));
        Assert.Equal(TenantRole.Operator, Tenants.RequiredRole("POST", "/api/v1/schedules/s1/run"));
        Assert.Equal(TenantRole.Admin, Tenants.RequiredRole("POST", "/api/v1/schedules"));
        Assert.Equal(TenantRole.Admin, Tenants.RequiredRole("PUT", "/api/v1/system/approvals"));
        Assert.Equal(TenantRole.Admin, Tenants.RequiredRole("GET", "/api/v1/tenant/keys"));
        Assert.Equal(TenantRole.Owner, Tenants.RequiredRole("POST", "/api/v1/tenant/keys"));
        Assert.Equal(TenantRole.Viewer, Tenants.RequiredRole("POST", "/api/v1/tenants"));
        Assert.True(Tenants.IsValidId("acme-2"));
        Assert.False(Tenants.IsValidId("_platform"));
        Assert.False(Tenants.IsValidId("A"));
    }
}
