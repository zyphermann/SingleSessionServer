using Microsoft.AspNetCore.Http;

internal static class IdentityHeaderWriter
{
    public static void WriteIdentityHeaders(
        HttpResponse res,
        string? playerId = null,
        string? playerShortId = null,
        string? deviceId = null)
    {
        SetHeader(res, RequestIdentityResolver.PlayerIdHeader, playerId);
        SetHeader(res, RequestIdentityResolver.PlayerShortIdHeader, playerShortId);
        SetHeader(res, RequestIdentityResolver.DeviceIdHeader, deviceId);
    }

    public static void WriteSessionHeader(HttpResponse res, string? sessionId) =>
        SetHeader(res, RequestIdentityResolver.SessionIdHeader, sessionId);

    public static void ClearSessionHeader(HttpResponse res) =>
        res.Headers.Remove(RequestIdentityResolver.SessionIdHeader);

    private static void SetHeader(HttpResponse res, string header, string? value)
    {
        if (string.IsNullOrWhiteSpace(header))
            return;

        if (string.IsNullOrWhiteSpace(value))
        {
            res.Headers.Remove(header);
        }
        else
        {
            res.Headers[header] = value;
        }
    }
}
