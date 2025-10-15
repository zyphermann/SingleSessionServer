using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

internal static class RequestIdentityResolver
{
    private static readonly string[] SessionIdHeaders = { "X-Session-Id", "SessionId", "sessionId", "session_id" };
    private static readonly string[] PlayerIdHeaders = { "X-Player-Id", "PlayerId", "playerId", "player_id" };
    private static readonly string[] PlayerShortIdHeaders = { "X-Player-Short-Id", "PlayerShortId", "playerShortId", "player_short_id" };
    private static readonly string[] DeviceIdHeaders = { "X-Device-Id", "DeviceId", "deviceId", "device_id" };

    private const string CachedShortIdKey = "__resolver.shortId";

    public static async Task<RequestIdentity> ResolveAsync(
        HttpRequest request,
        DeviceStore devices,
        bool requirePlayerId = false,
        bool requireSessionId = false,
        bool requireDeviceId = false,
        bool inspectBodyForShortId = false)
    {
        var sessionId = GetFromCookieOrHeader(request, "session_id", SessionIdHeaders);
        var deviceId = GetFromCookieOrHeader(request, "device_id", DeviceIdHeaders);

        Guid? playerId = null;
        var playerIdRaw = GetFromCookieOrHeader(request, "player_id", PlayerIdHeaders);
        if (!string.IsNullOrWhiteSpace(playerIdRaw) && Guid.TryParse(playerIdRaw, out var parsedPlayer))
            playerId = parsedPlayer;

        var playerShortId = GetFromHeader(request, PlayerShortIdHeaders);
        if (string.IsNullOrWhiteSpace(playerShortId))
            playerShortId = GetFromCookie(request, "playerShortId");

        if (string.IsNullOrWhiteSpace(playerShortId) && inspectBodyForShortId)
            playerShortId = await TryReadShortIdFromBodyAsync(request);

        if (!string.IsNullOrWhiteSpace(playerShortId))
            playerShortId = playerShortId.Trim();

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

    private static async Task<string?> TryReadShortIdFromBodyAsync(HttpRequest request)
    {
        if (!request.HttpContext.Items.TryGetValue(CachedShortIdKey, out var cached))
        {
            var shortId = await ReadShortIdInternalAsync(request);
            request.HttpContext.Items[CachedShortIdKey] = shortId;
            cached = shortId;
        }

        return cached as string;
    }

    private static async Task<string?> ReadShortIdInternalAsync(HttpRequest request)
    {
        if (request.ContentLength is null or <= 0)
            return null;

        if (!IsJson(request.ContentType))
            return null;

        request.EnableBuffering();
        request.Body.Position = 0;

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body);
            request.Body.Position = 0;

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var root = document.RootElement;
            if (TryGetString(root, "shortId", out var shortId))
                return shortId;
            if (TryGetString(root, "short_id", out shortId))
                return shortId;
            if (TryGetString(root, "playerShortId", out shortId))
                return shortId;
            if (TryGetString(root, "player_short_id", out shortId))
                return shortId;

            return null;
        }
        catch (JsonException)
        {
            request.Body.Position = 0;
            return null;
        }
        catch (IOException)
        {
            request.Body.Position = 0;
            return null;
        }
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string? value)
    {
        if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
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

internal sealed record RequestIdentity(Guid? PlayerId, string? PlayerShortId, string? SessionId, string? DeviceId);

internal sealed class RequestIdentityException : Exception
{
    public RequestIdentityException(string message) : base(message)
    {
    }
}
