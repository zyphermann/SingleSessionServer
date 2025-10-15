using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

sealed class SessionEnforcementMiddleware : IMiddleware
{
    private readonly SessionManager _sessionManager;
    private readonly DeviceStore _deviceStore;

    public SessionEnforcementMiddleware(SessionManager sessionManager, DeviceStore deviceStore)
    {
        _sessionManager = sessionManager;
        _deviceStore = deviceStore;
    }

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

        RequestIdentity identity;
        try
        {
            identity = await RequestIdentityResolver.ResolveAsync(
                context.Request,
                _deviceStore,
                requirePlayerId: true,
                requireSessionId: true);
        }
        catch (RequestIdentityException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("No session.");
            return;
        }

        var sessionId = identity.SessionId;
        var playerId = identity.PlayerId?.ToString();

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
