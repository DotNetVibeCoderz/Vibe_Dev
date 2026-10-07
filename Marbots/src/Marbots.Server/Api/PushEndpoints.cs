using Marbots.Abstractions;
using Marbots.Runtime;

namespace Marbots.Server.Api;

/// <summary>Remote push: register devices (FCM/APNs tokens or ntfy topics), list, remove, send a test.</summary>
public static class PushEndpoints
{
    public static void MapPush(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/push").WithTags("Push");
        api.MapGet("/config", (PushService push) => push.Config());
        api.MapGet("/devices", (PushService push, CancellationToken ct) => push.ListAsync(ct));
        api.MapPost("/devices", async (RegisterPushDeviceRequest req, PushService push, CancellationToken ct) =>
        {
            try { return Results.Ok(await push.RegisterAsync(req, ct)); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });
        api.MapDelete("/devices/{id}", async (string id, PushService push, CancellationToken ct) =>
            await push.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/test", (PushService push, CancellationToken ct) => push.TestAsync(ct));
    }
}
