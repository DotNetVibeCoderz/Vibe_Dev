using System.Net;
using System.Security.Claims;
using Marbots.Abstractions;
using Marbots.Runtime;
using Marbots.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Marbots.Server.Api;

/// <summary>Who-am-I, tenant management (platform admins), tenant keys and members (owners), and UI sign-in.</summary>
public static class TenantEndpoints
{
    public static void MapTenancy(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Tenancy");

        api.MapGet("/whoami", async (TenantAccessor t, AuthSettings s, PlatformStore platform, HttpContext ctx, CancellationToken ct) =>
        {
            IReadOnlyList<string> tenants = t.PlatformAdmin
                ? [.. (await platform.ListTenantsAsync(ct)).Where(x => !x.Disabled).Select(x => x.Id)]
                : MarbotsClaims.Read(ctx.User) is not null ? await AuthSetup.AccessibleTenantsAsync(ctx.User, platform, ct) : [t.TenantId];
            return new WhoAmI(t.TenantId, t.Role, t.User, t.PlatformAdmin, s.MultiTenant, tenants);
        });

        // Platform administration: every tenant.
        api.MapGet("/tenants", async (TenantAccessor t, PlatformStore platform, CancellationToken ct) =>
            t.PlatformAdmin ? Results.Ok(await platform.ListTenantsAsync(ct)) : Forbidden());
        api.MapPost("/tenants", async (CreateTenantRequest req, TenantAccessor t, PlatformStore platform, TenantRuntimeManager runtimes, CancellationToken ct) =>
        {
            if (!t.PlatformAdmin) return Forbidden();
            if (!runtimes.MultiTenant) return Results.Problem("Multi-tenant mode is off (Marbots:MultiTenant).", statusCode: 409);
            try
            {
                var tenant = await platform.CreateTenantAsync(req.Id, req.Name, ct);
                await runtimes.GetAsync(tenant.Id, ct);
                return Results.Created($"/api/v1/tenants/{tenant.Id}", tenant);
            }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
        });
        api.MapPost("/tenants/{id}/disable", (string id, TenantAccessor t, PlatformStore platform, TenantRuntimeManager runtimes, CancellationToken ct) =>
            SetDisabled(id, true, t, platform, runtimes, ct));
        api.MapPost("/tenants/{id}/enable", (string id, TenantAccessor t, PlatformStore platform, TenantRuntimeManager runtimes, CancellationToken ct) =>
            SetDisabled(id, false, t, platform, runtimes, ct));
        api.MapPost("/tenants/{id}/keys", async (string id, CreateApiKeyRequest req, TenantAccessor t, PlatformStore platform, CancellationToken ct) =>
        {
            if (!t.PlatformAdmin) return Forbidden();
            if (await platform.GetTenantAsync(id, ct) is null) return Results.NotFound();
            return Results.Ok(await platform.CreateKeyAsync(id, req.Name, req.Role, ct));
        });

        // The caller's own tenant (roles enforced by TenantMiddleware: Admin to read, Owner to change).
        api.MapGet("/tenant/keys", (TenantAccessor t, PlatformStore platform, CancellationToken ct) => platform.ListKeysAsync(t.TenantId, ct));
        api.MapPost("/tenant/keys", (CreateApiKeyRequest req, TenantAccessor t, PlatformStore platform, CancellationToken ct) =>
            platform.CreateKeyAsync(t.TenantId, req.Name, req.Role, ct));
        api.MapDelete("/tenant/keys/{id}", async (string id, TenantAccessor t, PlatformStore platform, CancellationToken ct) =>
            await platform.RevokeKeyAsync(t.TenantId, id, ct) ? Results.NoContent() : Results.NotFound());
        api.MapGet("/tenant/members", (TenantAccessor t, PlatformStore platform, CancellationToken ct) => platform.ListMembersAsync(t.TenantId, ct));
        api.MapPut("/tenant/members", async (SetMemberRequest req, TenantAccessor t, PlatformStore platform, CancellationToken ct) =>
        {
            try { return Results.Ok(await platform.SetMemberAsync(t.TenantId, req.Subject, req.Role, ct)); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });
        api.MapDelete("/tenant/members/{subject}", async (string subject, TenantAccessor t, PlatformStore platform, CancellationToken ct) =>
            await platform.RemoveMemberAsync(t.TenantId, subject, ct) ? Results.NoContent() : Results.NotFound());

        // UI sign-in.
        app.MapGet("/login", (HttpContext ctx, AuthSettings s, string? returnUrl, string? error) =>
        {
            var back = SafeReturn(returnUrl);
            if (s.Oidc) return Results.Challenge(new AuthenticationProperties { RedirectUri = back }, [OpenIdConnectDefaults.AuthenticationScheme]);
            if (!s.RequireUiLogin) return Results.Redirect(back);
            return Results.Content(LoginPage(back, error), "text/html; charset=utf-8");
        });
        app.MapPost("/login", async (HttpContext ctx, AuthSettings s, PlatformStore platform) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var back = SafeReturn(form["returnUrl"]);
            var key = form["key"].ToString().Trim();
            ClaimsIdentity? id = null;
            if (await platform.ValidateKeyAsync(key, ctx.RequestAborted) is { } k)
                id = MarbotsClaims.Identity(CookieAuthenticationDefaults.AuthenticationScheme, k.Tenant, k.Role, "key:" + k.Name, false, "key");
            else if (s.GlobalKey is { } g && key.Length > 0 &&
                     System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(g)))
                id = MarbotsClaims.Identity(CookieAuthenticationDefaults.AuthenticationScheme, Tenants.Default, TenantRole.Owner, "platform-key", true, "key");
            if (id is null) return Results.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(back)}");
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id));
            return Results.Redirect(back);
        });
        app.MapGet("/logout", async (HttpContext ctx, AuthSettings s) =>
        {
            ctx.Response.Cookies.Delete(MarbotsClaims.TenantCookie);
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (s.Oidc && ctx.User.Identity?.IsAuthenticated == true)
                return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]);
            return Results.Redirect(s.RequireUiLogin ? "/login" : "/");
        });
        // Switch the UI to another tenant the user may access.
        app.MapGet("/account/tenant/{id}", async (string id, HttpContext ctx, PlatformStore platform, string? returnUrl) =>
        {
            var back = SafeReturn(returnUrl);
            if (MarbotsClaims.Read(ctx.User) is not { } who) return Results.Redirect("/login");
            var allowed = await AuthSetup.AccessibleTenantsAsync(ctx.User, platform, ctx.RequestAborted);
            if (!allowed.Contains(id)) return Results.Redirect(back);
            ctx.Response.Cookies.Append(MarbotsClaims.TenantCookie, id, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true });
            if (ctx.User.Identity?.AuthenticationType == MarbotsClaims.LocalScheme) return Results.Redirect(back);
            var role = who.Platform ? TenantRole.Owner
                : (await platform.MembershipsAsync([ctx.User.FindFirst("email")?.Value, ctx.User.FindFirst("sub")?.Value], ctx.RequestAborted))
                    .FirstOrDefault(m => m.Tenant == id)?.Role ?? TenantRole.Viewer;
            var extra = ctx.User.Claims.Where(c => c.Type is "email" or "sub");
            var identity = MarbotsClaims.Identity(CookieAuthenticationDefaults.AuthenticationScheme, id, role, who.User, who.Platform,
                ctx.User.FindFirst(MarbotsClaims.Via)?.Value ?? "key", extra);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Redirect(back);
        });
    }

    private static async Task<IResult> SetDisabled(string id, bool disabled, TenantAccessor t, PlatformStore platform, TenantRuntimeManager runtimes, CancellationToken ct)
    {
        if (!t.PlatformAdmin) return Forbidden();
        try
        {
            var tenant = await platform.SetDisabledAsync(id, disabled, ct);
            if (tenant is null) return Results.NotFound();
            if (disabled) await runtimes.StopTenantAsync(id, ct);
            return Results.Ok(tenant);
        }
        catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
    }

    private static IResult Forbidden() => Results.Problem("Only platform administrators can manage tenants.", statusCode: 403);

    private static string SafeReturn(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal) ? url : "/";

    private static string LoginPage(string returnUrl, string? error) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Sign in · Marbots</title>
        <link href="https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wght@12..96,700&family=Plus+Jakarta+Sans:wght@400;600&display=swap" rel="stylesheet">
        <link rel="stylesheet" href="/app.css"><link rel="icon" type="image/svg+xml" href="/favicon.svg">
        <style>
          body{display:grid;place-items:center;min-height:100vh;padding:16px}
          .login{width:min(420px,100%);background:var(--panel);border:1px solid var(--line);border-radius:var(--radius-l);box-shadow:var(--shadow);padding:32px 28px}
          .login .row{display:flex;align-items:center;gap:12px;margin-bottom:22px}
          .login label{display:block;font-weight:600;margin:0 0 6px}
          .login input{width:100%;font:inherit;padding:10px 12px;border-radius:var(--radius-m);border:1px solid var(--line);background:var(--panel-2);color:var(--ink)}
          .login button{margin-top:16px;width:100%;font:600 15px var(--font-body);padding:11px;border:0;border-radius:var(--radius-m);background:var(--indigo);color:var(--indigo-ink);cursor:pointer}
          .login .err{background:var(--danger-soft);color:var(--danger);border-radius:var(--radius-s);padding:8px 10px;margin-bottom:14px}
          .login .foot{margin-top:18px;color:var(--muted);font-size:.85rem}
        </style></head>
        <body><main class="login">
          <div class="row"><span class="marble" style="--c:#2C3BA3;--s:40px" aria-hidden="true"></span>
          <div><h1 style="font-size:1.5rem">Marbots</h1><div style="color:var(--muted)">Sign in to your workspace · Masuk ke workspace</div></div></div>
          {{(error is null ? "" : "<div class=\"err\" role=\"alert\">That key was not accepted. · Kunci tidak diterima.</div>")}}
          <form method="post" action="/login">
            <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl)}}">
            <label for="key">API key</label>
            <input id="key" name="key" type="password" autocomplete="current-password" placeholder="mbk_…" required autofocus>
            <button type="submit">Sign in · Masuk</button>
          </form>
          <p class="foot">Ask a tenant owner for a key, or use the server's platform key. {{WebUtility.HtmlEncode(WellKnown.Credits)}}.</p>
        </main></body></html>
        """;
}
