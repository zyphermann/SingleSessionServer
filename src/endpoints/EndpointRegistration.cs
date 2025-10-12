using Microsoft.AspNetCore.Builder;

internal static class EndpointRegistration
{
    public static void MapApplicationEndpoints(this WebApplication app)
    {
        DeviceEndpoints.Map(app);
        SessionEndpoints.Map(app);
        GameEndpoints.Map(app);
        Ps3GameEndpoints.Map(app);
        WhoAmIEndpoint.Map(app);
        ServerInfo.Map(app);
    }
}
