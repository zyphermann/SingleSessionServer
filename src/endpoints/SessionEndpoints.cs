using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;

internal static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
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

            var playerIdString = identity.PlayerId!.Value.ToString();

            var ctx = await devices.TryGetAsync(playerIdString, identity.DeviceId);
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
            RequestIdentity identity;
            try
            {
                identity = await RequestIdentityResolver.ResolveAsync(req, devices, inspectBodyForShortId: true);
            }
            catch (RequestIdentityException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var shortId = identity.PlayerShortId;
            if (string.IsNullOrWhiteSpace(shortId))
                return Results.BadRequest(new { error = "shortId is required." });

            var context = await devices.EnsureAsync(identity.PlayerId?.ToString(), identity.DeviceId);
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

        app.MapPost("/session/login/direct", async (HttpRequest req, DirectLoginRequest request, DeviceStore devices, SessionManager sm) =>
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

            RequestIdentity identity;
            try
            {
                identity = await RequestIdentityResolver.ResolveAsync(req, devices);
            }
            catch (RequestIdentityException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? identity.DeviceId : request.DeviceId;
            var context = await devices.EnsureAsync(targetPlayer.Value.ToString(), deviceId);
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

sealed record DirectLoginRequest(string? PlayerId, string? PlayerShortId, string? DeviceId);
