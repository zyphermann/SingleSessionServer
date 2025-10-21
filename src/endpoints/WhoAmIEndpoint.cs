using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;

internal static class WhoAmIEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/whoami", async (HttpRequest req, DeviceStore devices) =>
        {
            RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices);
            if (identity is null)
            {
                identity = new RequestIdentity(null, null, null, null);
            }

            DeviceContext? ctx = null;
            if (identity.PlayerId is Guid resolvedPlayer)
            {
                ctx = await devices.TryGetAsync(resolvedPlayer.ToString(), identity.DeviceId);
            }

            return Results.Json(new
            {
                playerId = identity.PlayerId?.ToString(),
                playerShortId = ctx?.PlayerShortId ?? identity.PlayerShortId,
                deviceId = identity.DeviceId,
                sessionId = identity.SessionId
            });
        })
        .WithMetadata(EndpointAccessMetadata.Public);

        app.MapGet("/whoami/{sessionId:guid}", async (Guid sessionId, SessionManager sm) =>
        {
            var lookup = await sm.TryGetAsync(sessionId, extend: true);
            if (lookup is null)
                return Results.NotFound(new { error = "Session not found or expired." });

            return Results.Json(new
            {
                sessionId = sessionId.ToString(),
                playerId = lookup.Value.PlayerId.ToString(),
                deviceId = lookup.Value.DeviceId.ToString(),
                playerShortId = lookup.Value.PlayerShortId
            });
        })
        .WithMetadata(EndpointAccessMetadata.Public);
    }
}
