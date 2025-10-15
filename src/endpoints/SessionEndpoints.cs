using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;

internal static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
        // /session/login assumes the client already knows the player (via cookies or headers)
        // and just needs a fresh session token for the existing player/device combo.
        app.MapPost("/session/login", async (HttpRequest req, HttpResponse res, DeviceStore devices, SessionManager sm) =>
        {
            RequestIdentity identity;
            try
            {
                identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);
            }
            catch (RequestIdentityException)
            {
                return Results.BadRequest(new { error = "No player_id. Call /device/init first." });
            }

            var ctx = await devices.TryGetAsync(identity.PlayerId!.Value.ToString(), identity.DeviceId);
            if (ctx is null)
                return Results.BadRequest(new { error = "Unknown device. Call /device/init first." });

            var sessionId = await sm.CreateOrReplaceAsync(ctx.PlayerIdString, ctx.DeviceIdString, TimeSpan.FromHours(8));
            SetSessionCookies(res, req.Scheme, ctx.PlayerIdString, ctx.DeviceIdString, sessionId);
            return Results.Json(new { ok = true, playerId = ctx.PlayerIdString, playerShortId = ctx.PlayerShortId, sessionId });
        })
        .WithMetadata(EndpointAccessMetadata.Public);

        // /session/login/short links a device to a player when the client only has the short player code.
        // It looks up the player via shortId first, then issues cookies and a new session.
        app.MapPost("/session/login/short", async (HttpRequest req, HttpResponse res, DeviceStore devices, SessionManager sm) =>
        {
            RequestIdentity identity;
            try
            {
                identity = await RequestIdentityResolver.ResolveAsync(req, devices);
            }
            catch (RequestIdentityException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var context = await devices.EnsureAsync(identity.PlayerId?.ToString(), identity.DeviceId);

            var targetPlayerId = await devices.TryGetPlayerIdByShortIdAsync(identity.PlayerShortId);
            if (targetPlayerId is null)
                return Results.NotFound(new { error = "Unknown short id." });

            var bound = await devices.BindDeviceAsync(context, targetPlayerId.Value.ToString());

            var sessionId = await sm.CreateOrReplaceAsync(bound.PlayerIdString, bound.DeviceIdString, TimeSpan.FromHours(8));
            SetSessionCookies(res, req.Scheme, bound.PlayerIdString, bound.DeviceIdString, sessionId);
            return Results.Json(new { ok = true, playerId = bound.PlayerIdString, playerShortId = bound.PlayerShortId, sessionId });
        })
        .WithMetadata(EndpointAccessMetadata.Public);

        // /session/login/direct is used when the client sends playerId/playerShortId in the body and
        // may also supply a device id; it bypasses cookie requirements entirely.
        app.MapPost("/session/login/direct", async (HttpRequest req, HttpResponse res, DirectLoginRequest body, DeviceStore devices, SessionManager sm) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "Missing request body." });

            RequestIdentity identity;
            try
            {
                identity = await RequestIdentityResolver.ResolveAsync(
                    req,
                    devices);
            }
            catch (RequestIdentityException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (identity.PlayerId is not Guid targetPlayer)
                return Results.BadRequest(new { error = "Unknown player." });

            var deviceId = string.IsNullOrWhiteSpace(body.DeviceId) ? identity.DeviceId : body.DeviceId;
            var context = await devices.EnsureAsync(targetPlayer.ToString(), deviceId);
            if (context.PlayerId != targetPlayer)
                context = await devices.BindDeviceAsync(context, targetPlayer.ToString());

            var sessionId = await sm.CreateOrReplaceAsync(context.PlayerIdString, context.DeviceIdString, TimeSpan.FromHours(8));
            SetSessionCookies(res, req.Scheme, context.PlayerIdString, context.DeviceIdString, sessionId);

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

        app.MapPost("/session/logout", async (HttpRequest req, HttpResponse res, SessionManager sm, DeviceStore devices) =>
        {
            RequestIdentity identity;
            try
            {
                identity = await RequestIdentityResolver.ResolveAsync(req, devices);
            }
            catch (RequestIdentityException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (!string.IsNullOrWhiteSpace(identity.SessionId) && identity.PlayerId is Guid pid)
                await sm.RevokeIfActiveAsync(pid.ToString(), identity.SessionId);

            EndpointHelpers.DeleteCookie(res, "session_id");
            return Results.Json(new { ok = true });
        })
        .WithMetadata(EndpointAccessMetadata.Public);
    }

    private static void SetSessionCookies(HttpResponse res, string? scheme, string playerId, string deviceId, string sessionId)
    {
        var https = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);
        EndpointHelpers.SetCookie(res, "player_id", playerId, https, TimeSpan.FromDays(365));
        EndpointHelpers.SetCookie(res, "device_id", deviceId, https, TimeSpan.FromDays(365));
        EndpointHelpers.SetCookie(res, "session_id", sessionId, https, TimeSpan.FromHours(8));
    }
}

sealed record DirectLoginRequest(string? PlayerId, string? PlayerShortId, string? DeviceId);
