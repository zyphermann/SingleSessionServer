using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;

sealed class SessionEnforcementMiddleware : IMiddleware
{
    private readonly SessionManager _sessionManager;

    public SessionEnforcementMiddleware(SessionManager sessionManager) => _sessionManager = sessionManager;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var endpoint = context.GetEndpoint();
        var metadata = endpoint?.Metadata.GetMetadata<EndpointAccessMetadata>();
        var requiresSession = metadata?.RequiresSession ?? false;

        if (!requiresSession)
        {
            await next(context);
            return;
        }

        var sessionId = context.Request.Cookies["session_id"];
        var playerId = context.Request.Cookies["player_id"];

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(playerId))
        {
            var (bodyPlayerId, bodySessionId) = await TryExtractSessionAsync(context.Request);
            playerId ??= bodyPlayerId;
            sessionId ??= bodySessionId;
        }

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(playerId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("No session.");
            return;
        }

        var ok = await _sessionManager.ValidateAsync(playerId, sessionId, sliding: true);
        if (!ok)
        {
            EndpointHelpers.DeleteCookie(context.Response, "session_id");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Signed out: login from another device.");
            return;
        }

        await next(context);
    }

    private static async Task<(string? PlayerId, string? SessionId)> TryExtractSessionAsync(HttpRequest request)
    {
        if (request.ContentLength is null or <= 0)
            return (null, null);

        if (!HttpMethods.IsPost(request.Method) &&
            !HttpMethods.IsPut(request.Method) &&
            !HttpMethods.IsPatch(request.Method))
            return (null, null);

        if (request.ContentType is null ||
            !request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        request.EnableBuffering();
        request.Body.Position = 0;

        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var payload = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(payload))
            return (null, null);

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);

            var root = document.RootElement;

            string? playerId = ExtractString(root, "playerId")
                               ?? ExtractString(root, "player_id");

            string? sessionId = ExtractString(root, "sessionId")
                                ?? ExtractString(root, "session_id");

            return (playerId, sessionId);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ExtractString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            return null;

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
