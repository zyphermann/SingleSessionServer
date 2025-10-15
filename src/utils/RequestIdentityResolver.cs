using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

internal static class RequestIdentityResolver
{
    private static readonly string[] SessionIdHeaders = { "X-Session-Id", "SessionId", "sessionId", "session_id" };
    private static readonly string[] PlayerIdHeaders = { "X-Player-Id", "PlayerId", "playerId", "player_id" };
    private static readonly string[] PlayerShortIdHeaders = { "X-Player-Short-Id", "PlayerShortId", "playerShortId", "player_short_id", "shortId", "short_id" };
    private static readonly string[] DeviceIdHeaders = { "X-Device-Id", "DeviceId", "deviceId", "device_id" };

    private const string CachedBodyIdentityKey = "__resolver.bodyIdentity";

    public static async Task<RequestIdentity> ResolveAsync(
        HttpRequest request,
        DeviceStore devices,
        bool requirePlayerId = false,
        bool requireSessionId = false,
        bool requireDeviceId = false
       )
    {
        Guid? playerId = null;

        string? bodyPlayerId = null;
        string? bodyShortId = null;

        var sessionId = GetFromCookieOrHeader(request, "session_id", SessionIdHeaders);
        var deviceId = GetFromCookieOrHeader(request, "device_id", DeviceIdHeaders);
        var playerIdRaw = GetFromCookieOrHeader(request, "player_id", PlayerIdHeaders);
        var playerShortId = GetFromCookieOrHeader(request, "playerShortId", PlayerShortIdHeaders);

        if (!string.IsNullOrWhiteSpace(playerIdRaw) && Guid.TryParse(playerIdRaw, out var parsedPlayer))
            playerId = parsedPlayer;

        if (playerId is null)
        {
            var bodyIdentity = await TryReadBodyIdentityAsync(request);
            bodyPlayerId = bodyIdentity.PlayerId;
            bodyShortId = bodyIdentity.PlayerShortId;
        }

        if (playerId is null && !string.IsNullOrWhiteSpace(bodyPlayerId) && Guid.TryParse(bodyPlayerId, out var parsedBodyId))
            playerId = parsedBodyId;

        if (string.IsNullOrWhiteSpace(playerShortId) && !string.IsNullOrWhiteSpace(bodyShortId))
            playerShortId = bodyShortId.Trim();

        if (!string.IsNullOrWhiteSpace(playerShortId))
            playerShortId = playerShortId.Trim();

        if (string.IsNullOrWhiteSpace(playerShortId))
        {
            var context = await devices.TryGetAsync(playerId?.ToString(), deviceId);
            playerShortId = context?.PlayerShortId;
        }

        if (playerId is null && !string.IsNullOrWhiteSpace(playerShortId))
        {
            var resolved = await devices.TryGetPlayerIdByShortIdAsync(playerShortId);
            if (resolved is Guid guid && guid != Guid.Empty)
                playerId = guid;
        }

        if (requirePlayerId && playerId is null)
            throw new RequestIdentityException("Missing player id.");

        if (requireSessionId && string.IsNullOrWhiteSpace(sessionId))
            throw new RequestIdentityException("Missing session id.");

        if (requireDeviceId && string.IsNullOrWhiteSpace(deviceId))
            throw new RequestIdentityException("Missing device id.");

        return new RequestIdentity(playerId, playerShortId, sessionId, deviceId);
    }

    private static async Task<BodyIdentity> TryReadBodyIdentityAsync(HttpRequest request)
    {
        if (request.HttpContext.Items.TryGetValue(CachedBodyIdentityKey, out var cached) && cached is BodyIdentity identity)
            return identity;

        if (request.ContentLength is null or <= 0 || !IsJson(request.ContentType))
            return CacheIdentity(request, BodyIdentity.Empty);

        request.EnableBuffering();
        request.Body.Position = 0;

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body);
            request.Body.Position = 0;

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return CacheIdentity(request, BodyIdentity.Empty);

            var playerId = TryGetString(document.RootElement, out var pid, PlayerIdHeaders) ? pid : null;
            var shortId = TryGetString(document.RootElement, out var psid, PlayerShortIdHeaders) ? psid : null;

            return CacheIdentity(request, new BodyIdentity(playerId, shortId));
        }
        catch (JsonException)
        {
            request.Body.Position = 0;
            return CacheIdentity(request, BodyIdentity.Empty);
        }
        catch (IOException)
        {
            request.Body.Position = 0;
            return CacheIdentity(request, BodyIdentity.Empty);
        }
    }

    private static BodyIdentity CacheIdentity(HttpRequest request, BodyIdentity identity)
    {
        request.HttpContext.Items[CachedBodyIdentityKey] = identity;
        return identity;
    }

    private static bool TryGetString(JsonElement root, out string? value, params string[] propertyNames)
    {
        if (propertyNames is not null)
        {
            foreach (var name in propertyNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
                {
                    var s = element.GetString();
                    value = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
                    return value is not null;
                }
            }
        }

        value = null;
        return false;
    }

    private static bool IsJson(string? contentType)
        => contentType is not null && contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);

    private static string? GetFromCookieOrHeader(HttpRequest request, string cookieName, params string[] headerNames)
    {
        var value = GetFromCookie(request, cookieName);
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        return GetFromHeader(request, headerNames);
    }

    private static string? GetFromCookie(HttpRequest request, string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        return request.Cookies.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string? GetFromHeader(HttpRequest request, params string[] names)
    {
        if (names is null || names.Length == 0)
            return null;

        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name))
                continue;

            if (request.Headers.TryGetValue(name, out var values))
            {
                var candidate = values.ToString();
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate;
            }
        }

        return null;
    }
}

internal sealed record BodyIdentity(string? PlayerId, string? PlayerShortId)
{
    public static BodyIdentity Empty { get; } = new(null, null);
}

internal sealed record RequestIdentity(Guid? PlayerId, string? PlayerShortId, string? SessionId, string? DeviceId);

internal sealed class RequestIdentityException : Exception
{
    public RequestIdentityException(string message) : base(message)
    {
    }
}
