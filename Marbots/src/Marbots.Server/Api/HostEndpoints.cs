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
        api.MapPost("/enrollments", async (CreateEnrollmentRequest req, HostRegistry hosts, HttpContext ctx, CancellationToken ct) =>
        {
            var (token, expires) = await hosts.CreateEnrollmentAsync(req.Name, TimeSpan.FromMinutes(Math.Clamp(req.ValidMinutes ?? 60, 5, 24 * 60)), "api", ct);
            var server = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return new CreateEnrollmentResult(token, expires, $"marbots-host enroll --server {server} --token {token} --name \"{req.Name}\"");
        });

        // Called by marbots-host with the token (no API key).
        api.MapPost("/enroll", async (HostEnrollmentRequest req, HostRegistry hosts, HttpContext ctx, CancellationToken ct) =>
            await hosts.EnrollAsync(req, $"token from {ctx.Connection.RemoteIpAddress}", ct) is { } r ? Results.Ok(r) : Results.Json(new { detail = "Invalid or expired enrollment token." }, statusCode: 401));

        // The host's long-lived connection (authenticated by host id + secret headers).
        api.Map("/connect", async (HttpContext ctx, HostRegistry hosts, HostConnectionManager connections) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest(new { detail = "WebSocket expected." });
            var host = await hosts.AuthenticateAsync(ctx.Request.Headers[HostProtocol.HostIdHeader].ToString(), ctx.Request.Headers[HostProtocol.HostSecretHeader].ToString(), ctx.RequestAborted);
            if (host is null) return Results.Unauthorized();
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await connections.RunAsync(host, socket, ctx.RequestAborted);
            return Results.Empty;
        });

        // SSH bootstrap: credentials are used for this request only.
        api.MapPost("/bootstrap", (SshBootstrapRequest req, HostBootstrapper b, CancellationToken ct) => b.BootstrapAsync(req, "api", ct));

        api.MapGet("/packages", (HostBootstrapper b) => new
        {
            directory = b.PackagesDirectory,
            available = new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64" }.Where(r => b.PackageFor(r) is not null),
        });

        api.MapPost("/{id}/disable", async (string id, HostRegistry hosts, CancellationToken ct) => await SetDisabled(hosts, id, true, ct));
        api.MapPost("/{id}/enable", async (string id, HostRegistry hosts, CancellationToken ct) => await SetDisabled(hosts, id, false, ct));
        api.MapDelete("/{id}", async (string id, HostRegistry hosts, CancellationToken ct) => await hosts.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private static async Task<IResult> SetDisabled(HostRegistry hosts, string id, bool disabled, CancellationToken ct)
    {
        if (await hosts.GetAsync(id, ct) is not { } h) return Results.NotFound();
        h.Disabled = disabled;
        await hosts.SaveAsync(h, ct);
        return Results.NoContent();
    }
}
