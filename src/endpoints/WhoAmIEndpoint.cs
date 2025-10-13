using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;

internal static class WhoAmIEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/whoami", async (HttpRequest req, DeviceStore devices) =>
        {
            req.Cookies.TryGetValue("player_id", out var pid);
            req.Cookies.TryGetValue("device_id", out var did);
            req.Cookies.TryGetValue("session_id", out var sid);
            var ctx = await devices.TryGetAsync(pid, did);
            return Results.Json(new
            {
                playerId = pid,
                playerShortId = ctx?.PlayerShortId,
                deviceId = did,
                session_id = sid
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
