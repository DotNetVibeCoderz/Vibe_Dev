using Marbots.Abstractions;
using Marbots.Runtime;

namespace Marbots.Server.Api;

/// <summary>Distributed agent hosts: enrollment, the host WebSocket, SSH bootstrap and management.</summary>
public static class HostEndpoints
{
    public static void MapHosts(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/hosts");

        // One-time token for a manual install: marbots-host enroll --server <url> --token <token>
        api.MapPost("/enrollments", async (CreateEnrollmentRequest req, HostRegistry hosts, MarbotsOptions options, HttpContext ctx, CancellationToken ct) =>
        {
            var (token, expires) = await hosts.CreateEnrollmentAsync(req.Name, TimeSpan.FromMinutes(Math.Clamp(req.ValidMinutes ?? 60, 5, 24 * 60)), "api", ct);
            var server = Tenants.ServerUrlFor($"{ctx.Request.Scheme}://{ctx.Request.Host}", options.TenantId);
            return new CreateEnrollmentResult(token, expires, $"marbots-host enroll --server {server} --token {token} --name \"{req.Name}\"");
        });

        // Called by marbots-host with the token (no API key).
        api.MapPost("/enroll", async (HostEnrollmentRequest req, HostRegistry hosts, HttpContext ctx, CancellationToken ct) =>
            await hosts.EnrollAsync(req, $"token from {ctx.Connection.RemoteIpAddress}", ct) is { } r ? Results.Ok(r) : Results.Json(new { detail = "Invalid or expired enrollment token." }, statusCode: 401));

        // The host's long-lived connection (authenticated by host id + secret headers).
        api.Map("/connect", async (HttpContext ctx, HostRegistry hosts, HostConnectionManager connections, MarbotsOptions options, ILoggerFactory logs) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest(new { detail = "WebSocket expected." });
            var host = await hosts.AuthenticateAsync(ctx.Request.Headers[HostProtocol.HostIdHeader].ToString(), ctx.Request.Headers[HostProtocol.HostSecretHeader].ToString(), ctx.RequestAborted);
            if (host is null) return Results.Unauthorized();
            if (await CertificateProblemAsync(ctx, host, hosts, options.HostSecurity) is { } problem)
            {
                logs.CreateLogger("Marbots.Hosts").LogWarning("Host {Host} refused: {Reason}", host.Id, problem);
                return Results.Json(new { detail = problem }, statusCode: 401);
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await connections.RunAsync(host, socket, ctx.RequestAborted);
            return Results.Empty;
        });

        // Client-certificate renewal (host id + secret headers; the current certificate too when one is required).
        api.MapPost("/renew", async (HostCertificateRenewal req, HttpContext ctx, HostRegistry hosts, MarbotsOptions options, CancellationToken ct) =>
        {
            var host = await hosts.AuthenticateAsync(ctx.Request.Headers[HostProtocol.HostIdHeader].ToString(), ctx.Request.Headers[HostProtocol.HostSecretHeader].ToString(), ct);
            if (host is null) return Results.Unauthorized();
            if (host.CertificateThumbprint is not null && await CertificateProblemAsync(ctx, host, hosts, options.HostSecurity) is { } problem)
                return Results.Json(new { detail = problem }, statusCode: 401);
            try { return Results.Ok(await hosts.RenewCertificateAsync(host, req.Csr, ct)); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException)
            {
                return Results.Problem("Invalid certificate signing request: " + ex.Message, statusCode: 400);
            }
        });

        // SSH bootstrap: credentials are used for this request only.
        api.MapPost("/bootstrap", (SshBootstrapRequest req, HostBootstrapper b, CancellationToken ct) => b.BootstrapAsync(req, "api", ct));

        api.MapGet("/packages", (HostBootstrapper b) => new
        {
            directory = b.PackagesDirectory,
            available = new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64" }.Where(r => b.PackageFor(r) is not null),
        });

        api.MapPost("/{id}/disable", async (string id, HostRegistry hosts, CancellationToken ct) => await SetDisabled(hosts, id, true, ct));
        api.MapPost("/{id}/enable", async (string id, HostRegistry hosts, CancellationToken ct) => await SetDisabled(hosts, id, false, ct));
        api.MapDelete("/{id}", async (string id, HostRegistry hosts, CancellationToken ct) => await hosts.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }

    /// <summary>
    /// Null when the connection's client certificate is acceptable: valid for this host, or absent while not required.
    /// The certificate comes from the TLS handshake, or from a trusted proxy header when configured.
    /// </summary>
    private static async Task<string?> CertificateProblemAsync(HttpContext ctx, HostRecord host, HostRegistry hosts, HostSecurityOptions security)
    {
        var cert = await ctx.Connection.GetClientCertificateAsync(ctx.RequestAborted);
        if (cert is null && security.ClientCertificateHeader is { Length: > 0 } header)
            cert = HostCertificateAuthority.FromHeader(ctx.Request.Headers[header].ToString());
        if (cert is null)
            return security.RequireClientCertificate ? "a client certificate is required (re-enroll the host, or renew with marbots-host renew)" : null;
        return hosts.Authority.Validate(cert, host.Id, host.CertificateThumbprint);
    }

    private static async Task<IResult> SetDisabled(HostRegistry hosts, string id, bool disabled, CancellationToken ct)
    {
        if (await hosts.GetAsync(id, ct) is not { } h) return Results.NotFound();
        h.Disabled = disabled;
        await hosts.SaveAsync(h, ct);
        return Results.NoContent();
    }
}
