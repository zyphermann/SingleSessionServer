using System.Net;


// --- Utilities & Services ---
record EmailRequest(string Email);

internal static class DeviceEndpoints
{

    public static void Map(WebApplication app)
    {
        app.MapPost(EndpointPaths.Init, async (HttpRequest req, HttpResponse res, DeviceStore devices) =>
        {
            req.Cookies.TryGetValue("player_id", out var playerCookie);
            req.Cookies.TryGetValue("device_id", out var deviceCookie);

            var context = await devices.EnsureAsync(playerCookie, deviceCookie);
            var https = string.Equals(req.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            EndpointHelpers.SetCookie(res, "player_id", context.PlayerIdString, https, TimeSpan.FromDays(365));
            EndpointHelpers.SetCookie(res, "device_id", context.DeviceIdString, https, TimeSpan.FromDays(365));
            return Results.Json(new { playerId = context.PlayerIdString, deviceId = context.DeviceIdString });
        })
        .WithMetadata(EndpointAccessMetadata.Public);

        app.MapPost(EndpointPaths.TransferStart, async (
            HttpRequest req,
            HttpResponse res,
            DeviceStore devices,
            TokenStore tokens,
            IEmailSender mailer,
            IConfiguration cfg) =>
        {
            req.Cookies.TryGetValue("player_id", out var playerCookie);
            req.Cookies.TryGetValue("device_id", out var deviceCookie);

            var context = await devices.EnsureAsync(playerCookie, deviceCookie);
            var https = string.Equals(req.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            EndpointHelpers.SetCookie(res, "player_id", context.PlayerIdString, https, TimeSpan.FromDays(365));
            EndpointHelpers.SetCookie(res, "device_id", context.DeviceIdString, https, TimeSpan.FromDays(365));

            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<EmailRequest>(req.Body);
            if (body is null || string.IsNullOrWhiteSpace(body.Email) || !IsLikelyEmail(body.Email))
                return Results.BadRequest(new { error = "Invalid email" });

            body = body with { Email = body.Email.Trim() };

            var updatedContext = await devices.AttachEmailAsync(context, body.Email);

            EndpointHelpers.SetCookie(res, "player_id", updatedContext.PlayerIdString, https, TimeSpan.FromDays(365));
            EndpointHelpers.SetCookie(res, "device_id", updatedContext.DeviceIdString, https, TimeSpan.FromDays(365));

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

        app.MapGet(EndpointPaths.TransferAccept, async (
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

            req.Cookies.TryGetValue("player_id", out var playerCookie);
            req.Cookies.TryGetValue("device_id", out var deviceCookie);

            var context = await devices.EnsureAsync(playerCookie, deviceCookie);
            var bound = await devices.BindDeviceAsync(context, playerId);

            var https = string.Equals(req.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            EndpointHelpers.SetCookie(res, "player_id", bound.PlayerIdString, https, TimeSpan.FromDays(365));
            EndpointHelpers.SetCookie(res, "device_id", bound.DeviceIdString, https, TimeSpan.FromDays(365));

            var sessionId = await sm.CreateOrReplaceAsync(bound.PlayerIdString, TimeSpan.FromHours(8));
            EndpointHelpers.SetCookie(res, "session_id", sessionId, https, TimeSpan.FromHours(8));

            return Results.Text("Transfer OK. This browser is now the only active session.");
        })
        .WithMetadata(EndpointAccessMetadata.Public);
    }

    private static bool IsLikelyEmail(string email)
    {
        try { var addr = new System.Net.Mail.MailAddress(email); return addr.Address == email; }
        catch { return false; }
    }
}
