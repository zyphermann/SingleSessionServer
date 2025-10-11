using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System;
using System.Net;

internal static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        app.MapPost("/device/init", async (HttpRequest req, HttpResponse res, DeviceStore devices) =>
        {
            req.Cookies.TryGetValue("player_id", out var pid);
            var actual = await devices.EnsureAsync(pid);
            var https = string.Equals(req.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            EndpointHelpers.SetCookie(res, "player_id", actual, https, TimeSpan.FromDays(365));
            return Results.Json(new { playerId = actual });
        });

        app.MapPost("/device/transfer/start", async (
            HttpRequest req,
            HttpResponse res,
            DeviceStore devices,
            TokenStore tokens,
            IEmailSender mailer,
            IConfiguration cfg) =>
        {
            req.Cookies.TryGetValue("player_id", out var pid);
            var actual = await devices.EnsureAsync(pid);
            var https = string.Equals(req.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            EndpointHelpers.SetCookie(res, "player_id", actual, https, TimeSpan.FromDays(365));

            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<EmailRequest>(req.Body);
            if (body is null || string.IsNullOrWhiteSpace(body.Email) || !IsLikelyEmail(body.Email))
                return Results.BadRequest(new { error = "Invalid email" });

            body = body with { Email = body.Email.Trim() };

            var emailAttached = await devices.TrySetEmailAsync(actual, body.Email);
            if (!emailAttached)
                return Results.Conflict(new { error = "Email already registered" });

            var token = tokens.CreateToken(actual, TimeSpan.FromMinutes(10)); // one-time, 10 min
            var baseUrl = cfg.GetValue<string>("App:PublicBaseUrl")?.TrimEnd('/')
                         ?? $"{req.Scheme}://{req.Host.ToUriComponent()}";
            var link = $"{baseUrl}/device/transfer/accept?token={Uri.EscapeDataString(token)}";

            var html = $@"
                <p>Hi! Click this link to continue your session in the browser where you opened this email:</p>
                <p><a href=""{link}"">{WebUtility.HtmlEncode(link)}</a></p>
                <p>This link is valid for 10 minutes and can be used only once.</p>";

            await mailer.SendAsync(body.Email, "Your Magic Link", html);

            return Results.Json(new { ok = true });
        });

        app.MapGet("/device/transfer/accept", async (
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

            await devices.TouchAsync(playerId);

            var https = string.Equals(req.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            EndpointHelpers.SetCookie(res, "player_id", playerId, https, TimeSpan.FromDays(365));

            var sessId = await sm.CreateOrReplaceAsync(playerId, TimeSpan.FromHours(8));
            EndpointHelpers.SetCookie(res, "sess_id", sessId, https, TimeSpan.FromHours(8));

            return Results.Text("Transfer OK. This browser is now the only active session.");
        });
    }

    private static bool IsLikelyEmail(string email)
    {
        try { var addr = new System.Net.Mail.MailAddress(email); return addr.Address == email; }
        catch { return false; }
    }
}
