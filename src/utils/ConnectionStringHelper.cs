using Microsoft.Extensions.Configuration;
using System;

internal static class ConnectionStringHelper
{
    public static string? TryBuildFromEnvironment(IConfiguration configuration)
    {
        var database = configuration["DB_DATABASE"];
        var user = configuration["DB_USER"];
        var password = configuration["DB_PASSWORD"];

        if (string.IsNullOrWhiteSpace(database) ||
            string.IsNullOrWhiteSpace(user) ||
            string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var host = configuration["DB_HOST"];
        var port = configuration["DB_PORT"];

        host = string.IsNullOrWhiteSpace(host) ? "localhost" : host;
        port = string.IsNullOrWhiteSpace(port) ? "5432" : port;

        return $"Host={host};Port={port};Database={database};Username={user};Password={password};SSL Mode=Disable;Trust Server Certificate=true";
    }
}
