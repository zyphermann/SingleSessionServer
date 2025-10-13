using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

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
}
