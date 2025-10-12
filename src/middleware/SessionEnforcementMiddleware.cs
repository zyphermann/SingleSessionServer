using Microsoft.AspNetCore.Http;
using System;
using System.Threading.Tasks;

sealed class SessionEnforcementMiddleware
{
    private readonly RequestDelegate _next;

    public SessionEnforcementMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, SessionManager sessionManager)
    {
        var endpoint = context.GetEndpoint();
        var metadata = endpoint?.Metadata.GetMetadata<EndpointAccessMetadata>();
        var requiresSession = metadata?.RequiresSession ?? false;

        if (!requiresSession)
        {
            await _next(context);
            return;
        }

        var sessionId = context.Request.Cookies["session_id"];
        var playerId = context.Request.Cookies["player_id"];

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(playerId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("No session.");
            return;
        }

        var ok = await sessionManager.ValidateAsync(playerId, sessionId, sliding: true);
        if (!ok)
        {
            EndpointHelpers.DeleteCookie(context.Response, "session_id");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Signed out: login from another device.");
            return;
        }

        await _next(context);
    }
}
