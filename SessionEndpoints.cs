using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;

internal static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapPost("/session/login", async (HttpRequest req, HttpResponse res, DeviceStore devices, SessionManager sm) =>
        {
            if (!req.Cookies.TryGetValue("player_id", out var pid) || string.IsNullOrWhiteSpace(pid))
                return Results.BadRequest(new { error = "No player_id. Call /device/init first." });

            await devices.TouchAsync(pid);

            var https = string.Equals(req.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            var sessId = await sm.CreateOrReplaceAsync(pid, TimeSpan.FromHours(8));
            EndpointHelpers.SetCookie(res, "sess_id", sessId, https, TimeSpan.FromHours(8));
            return Results.Json(new { ok = true, playerId = pid, sessionId = sessId });
        });

        app.MapPost("/session/logout", async (HttpRequest req, HttpResponse res, SessionManager sm) =>
        {
            var sessId = req.Cookies["sess_id"];
            var pid = req.Cookies["player_id"];
            if (!string.IsNullOrWhiteSpace(sessId) && !string.IsNullOrWhiteSpace(pid))
                await sm.RevokeIfActiveAsync(pid, sessId);

            EndpointHelpers.DeleteCookie(res, "sess_id");
            return Results.Json(new { ok = true });
        });
    }
}
