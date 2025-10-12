

internal static class ServerInfo
{
    static DateTimeOffset startTimeUtc = DateTimeOffset.UtcNow;
    public static void Map(WebApplication app)
    {
        app.MapGet("/server/info", (IHostEnvironment env) =>
        {
            var uptime = DateTimeOffset.UtcNow - startTimeUtc;
            return Results.Json(new
            {
                application = env.ApplicationName,
                environment = env.EnvironmentName,
                startedAtUtc = startTimeUtc,
                uptimeSeconds = (long)uptime.TotalSeconds,
                machine = Environment.MachineName,
                processId = Environment.ProcessId
            });
        })
        .WithMetadata(EndpointAccessMetadata.Public);
    }
}