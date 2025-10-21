using System.Net;

internal static class DeviceEndpoints
{

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/device")
            .WithMetadata(EndpointAccessMetadata.Public);

        group.MapPost(
            EndpointPaths.Init,
            async (HttpRequest req, HttpResponse res, DeviceStore devices) =>
            {
                var context = await EnsureDeviceAsync(req, devices);

                IdentityHeaderWriter.WriteIdentityHeaders(
                    res,
                    context.PlayerIdString,
                    context.PlayerShortId,
                    context.DeviceIdString);
                return Results.Json(new { playerId = context.PlayerIdString, playerShortId = context.PlayerShortId, deviceId = context.DeviceIdString });
            })
            .WithMetadata(EndpointAccessMetadata.Public);

        group.MapPost(
            EndpointPaths.TransferStart,
            async (
                HttpRequest req,
                HttpResponse res,
                DeviceStore devices,
                TokenStore tokens,
                IEmailSender mailer,
                IConfiguration cfg) =>
            {
                var context = await EnsureDeviceAsync(req, devices);

                IdentityHeaderWriter.WriteIdentityHeaders(
                    res,
                    context.PlayerIdString,
                    context.PlayerShortId,
                    context.DeviceIdString);

                var body = await EmailRequest.TryReadAsync(req);
                if (body is null || body.Email is null)
                    return Results.BadRequest(new { error = "Invalid email" });

                var updatedContext = await devices.AttachEmailAsync(context, body.Email);

                IdentityHeaderWriter.WriteIdentityHeaders(
                    res,
                    updatedContext.PlayerIdString,
                    updatedContext.PlayerShortId,
                    updatedContext.DeviceIdString);

                var token = tokens.CreateToken(updatedContext.PlayerIdString, TimeSpan.FromMinutes(10)); // one-time, 10 min
                var baseUrl = cfg.GetValue<string>("App:PublicBaseUrl")?.TrimEnd('/')
                            ?? $"{req.Scheme}://{req.Host.ToUriComponent()}";
                var link = $"{baseUrl}/device/transfer/accept?token={Uri.EscapeDataString(token)}";

                var html = $@"
                    <p>Hi! Click this link to continue your session in the browser where you opened this email:</p>
                    <p><a href=""{link}"">{WebUtility.HtmlEncode(link)}</a></p>
                    <p>This link is valid for 10 minutes and can be used only once.</p>";

                await mailer.SendAsync(body.Email, "Your Magic Link", html);

                return Results.Json(new { ok = true });
            })
            .WithMetadata(EndpointAccessMetadata.Public);

        group.MapGet(
            EndpointPaths.TransferAccept,
            async (
                HttpRequest req,
                HttpResponse res,
                DeviceStore devices,
                TokenStore tokens,
                SessionManager sm) =>
            {
                var token = req.Query["token"].ToString();
                if (string.IsNullOrWhiteSpace(token))
                    return Results.BadRequest("Missing token");

                var playerId = tokens.ConsumeToken(token);
                if (playerId is null)
                    return Results.StatusCode(StatusCodes.Status410Gone);

                var context = await EnsureDeviceAsync(req, devices);
                var bound = await devices.BindDeviceAsync(context, playerId);

                var sessionId = await sm.CreateOrReplaceAsync(bound.PlayerIdString, bound.DeviceIdString, TimeSpan.FromHours(8));

                IdentityHeaderWriter.WriteIdentityHeaders(
                    res,
                    bound.PlayerIdString,
                    bound.PlayerShortId,
                    bound.DeviceIdString);
                IdentityHeaderWriter.WriteSessionHeader(res, sessionId);

                return Results.Text("Transfer OK. This browser is now the only active session.");
            })
            .WithMetadata(EndpointAccessMetadata.Public);
    }

    private static async Task<DeviceContext> EnsureDeviceAsync(HttpRequest req, DeviceStore devices)
    {
        var playerId = GetHeaderValue(req, RequestIdentityResolver.PlayerIdHeader);
        var deviceId = GetHeaderValue(req, RequestIdentityResolver.DeviceIdHeader);
        return await devices.EnsureAsync(playerId, deviceId);
    }

    private static string? GetHeaderValue(HttpRequest req, string header)
    {
        if (!req.Headers.TryGetValue(header, out var value))
            return null;

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

sealed class EmailRequest
{
    public string? Email { get; init; }

    public static async Task<EmailRequest?> TryReadAsync(HttpRequest request)
    {
        EmailRequest? payload;
        try
        {
            payload = await System.Text.Json.JsonSerializer.DeserializeAsync<EmailRequest>(request.Body);
        }
        catch (System.Text.Json.JsonException)
        {
            request.Body.Position = 0;
            return null;
        }

        request.Body.Position = 0;

        if (payload is null || string.IsNullOrWhiteSpace(payload.Email))
            return null;

        var trimmed = payload.Email.Trim();
        if (!IsLikelyEmail(trimmed))
            return null;

        return new EmailRequest { Email = trimmed };
    }

    private static bool IsLikelyEmail(string email)
    {
        try { var addr = new System.Net.Mail.MailAddress(email); return addr.Address == email; }
        catch { return false; }
    }
}
