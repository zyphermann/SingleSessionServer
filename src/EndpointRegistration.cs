using Microsoft.AspNetCore.Builder;

internal static class EndpointRegistration
{
    public static void MapApplicationEndpoints(this WebApplication app)
    {
        app.MapDeviceEndpoints();
        app.MapSessionEndpoints();
        app.MapGameEndpoints();
        app.MapPs3GameEndpoints();
    }
}
