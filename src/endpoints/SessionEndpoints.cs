using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;

internal static class SessionEndpoints
{
    public static void Map(WebApplication app)
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

            var sessionId = await sm.CreateOrReplaceAsync(ctx.PlayerIdString, ctx.DeviceIdString, TimeSpan.FromHours(8));
            EndpointHelpers.SetCookie(res, "session_id", sessionId, https, TimeSpan.FromHours(8));
            return Results.Json(new { ok = true, playerId = ctx.PlayerIdString, playerShortId = ctx.PlayerShortId, sessionId });
        })
        .WithMetadata(EndpointAccessMetadata.Public);

        app.MapPost("/session/login/short", async (HttpRequest req, HttpResponse res, DeviceStore devices, SessionManager sm) =>
        {
            var payload = await System.Text.Json.JsonSerializer.DeserializeAsync<ShortCodeLoginRequest>(req.Body);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ShortId))
                return Results.BadRequest(new { error = "shortId is required." });

            var shortId = payload.ShortId.Trim();

            req.Cookies.TryGetValue("player_id", out var playerCookie);
            req.Cookies.TryGetValue("device_id", out var deviceCookie);

            var context = await devices.EnsureAsync(playerCookie, deviceCookie);
            var targetPlayerId = await devices.TryGetPlayerIdByShortIdAsync(shortId);
            if (targetPlayerId is null)
                return Results.NotFound(new { error = "Unknown short id." });

            var bound = await devices.BindDeviceAsync(context, targetPlayerId.Value.ToString());

            var https = string.Equals(req.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            EndpointHelpers.SetCookie(res, "player_id", bound.PlayerIdString, https, TimeSpan.FromDays(365));
            EndpointHelpers.SetCookie(res, "device_id", bound.DeviceIdString, https, TimeSpan.FromDays(365));

            var sessionId = await sm.CreateOrReplaceAsync(bound.PlayerIdString, bound.DeviceIdString, TimeSpan.FromHours(8));
            EndpointHelpers.SetCookie(res, "session_id", sessionId, https, TimeSpan.FromHours(8));

            return Results.Json(new { ok = true, playerId = bound.PlayerIdString, playerShortId = bound.PlayerShortId, sessionId });
        })
        .WithMetadata(EndpointAccessMetadata.Public);

        app.MapPost("/session/logout", async (HttpRequest req, HttpResponse res, SessionManager sm) =>
        {
            var sessionId = req.Cookies["session_id"];
            var pid = req.Cookies["player_id"];
            if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(pid))
                await sm.RevokeIfActiveAsync(pid, sessionId);

            EndpointHelpers.DeleteCookie(res, "session_id");
            return Results.Json(new { ok = true });
        })
        .WithMetadata(EndpointAccessMetadata.Public);

        app.MapPost("/session/login/direct", async (DirectLoginRequest request, DeviceStore devices, SessionManager sm) =>
        {
            if (request is null)
                return Results.BadRequest(new { error = "Missing request body." });

            Guid? targetPlayer = null;

            if (!string.IsNullOrWhiteSpace(request.PlayerId) && Guid.TryParse(request.PlayerId, out var parsed))
                targetPlayer = parsed;
            else if (!string.IsNullOrWhiteSpace(request.PlayerShortId))
                targetPlayer = await devices.TryGetPlayerIdByShortIdAsync(request.PlayerShortId.Trim());

            if (targetPlayer is null)
                return Results.BadRequest(new { error = "Unknown player." });

            var context = await devices.EnsureAsync(targetPlayer.Value.ToString(), request.DeviceId);
            if (context.PlayerId != targetPlayer.Value)
                context = await devices.BindDeviceAsync(context, targetPlayer.Value.ToString());

            var sessionId = await sm.CreateOrReplaceAsync(context.PlayerIdString, context.DeviceIdString, TimeSpan.FromHours(8));

            return Results.Json(new
            {
                ok = true,
                playerId = context.PlayerIdString,
                playerShortId = context.PlayerShortId,
                deviceId = context.DeviceIdString,
                sessionId
            });
        })
        .WithMetadata(EndpointAccessMetadata.Public);
    }
}

sealed record ShortCodeLoginRequest(string ShortId);

sealed record DirectLoginRequest(string? PlayerId, string? PlayerShortId, string? DeviceId);
