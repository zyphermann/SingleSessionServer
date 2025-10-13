using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
    }
}
