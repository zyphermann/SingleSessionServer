using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;

internal static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapPost("/session/login", async (HttpRequest req, HttpResponse res, DeviceStore devices, SessionManager sm) =>
        {
            if (!req.Cookies.TryGetValue("player_id", out var playerCookie) || string.IsNullOrWhiteSpace(playerCookie))
                return Results.BadRequest(new { error = "No player_id. Call /device/init first." });

            req.Cookies.TryGetValue("device_id", out var deviceCookie);
            var ctx = await devices.TryGetAsync(playerCookie, deviceCookie);
            if (ctx is null)
                return Results.BadRequest(new { error = "Unknown device. Call /device/init first." });

            var https = string.Equals(req.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            EndpointHelpers.SetCookie(res, "player_id", ctx.PlayerIdString, https, TimeSpan.FromDays(365));
            EndpointHelpers.SetCookie(res, "device_id", ctx.DeviceIdString, https, TimeSpan.FromDays(365));

            var sessId = await sm.CreateOrReplaceAsync(ctx.PlayerIdString, TimeSpan.FromHours(8));
            EndpointHelpers.SetCookie(res, "sess_id", sessId, https, TimeSpan.FromHours(8));
            return Results.Json(new { ok = true, playerId = ctx.PlayerIdString, sessionId = sessId });
        })
        .WithMetadata(EndpointAccessMetadata.Public);

        app.MapPost("/session/logout", async (HttpRequest req, HttpResponse res, SessionManager sm) =>
        {
            var sessId = req.Cookies["sess_id"];
            var pid = req.Cookies["player_id"];
            if (!string.IsNullOrWhiteSpace(sessId) && !string.IsNullOrWhiteSpace(pid))
                await sm.RevokeIfActiveAsync(pid, sessId);

            EndpointHelpers.DeleteCookie(res, "sess_id");
            return Results.Json(new { ok = true });
        })
        .WithMetadata(EndpointAccessMetadata.Public);
    }
}
