using System.Text.Json;

internal static class EmailVerificationEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/email/verification");

        group.MapPost("/start", async (HttpRequest req, EmailVerificationService verifications, IEmailSender sender, IConfiguration cfg, DeviceStore devices) =>
        {
            RequestIdentity? identity = await RequestIdentityResolver.ResolveAsync(req, devices, requirePlayerId: true);

            if (identity is null || identity.PlayerId is null)
            {
                return Results.BadRequest(new { error = "Missing or invalid player id." });
            }

            var playerId = identity.PlayerId.Value;

            EmailVerificationRequest? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<EmailVerificationRequest>(req.Body);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON payload." });
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.Email))
                return Results.BadRequest(new { error = "Email is required." });

            var trimmedEmail = payload.Email.Trim();
            if (!IsLikelyEmail(trimmedEmail))
                return Results.BadRequest(new { error = "Email format is invalid." });

            var pending = await verifications.CreateAsync(playerId, trimmedEmail, TimeSpan.FromHours(24));

            var baseUrl = cfg.GetValue<string>("App:PublicBaseUrl")?.TrimEnd('/')
                           ?? $"{req.Scheme}://{req.Host.ToUriComponent()}";
            var link = $"{baseUrl}/email/verification/confirm?token={Uri.EscapeDataString(pending.Token.ToString())}";

            var html = $"""
                <p>Hi!</p>
                <p>Please confirm your email address by clicking the link below:</p>
                <p><a href="{link}">{link}</a></p>
                <p>This link expires on {pending.ExpiresAt:u}.</p>
                """;

            await sender.SendAsync(trimmedEmail, "Confirm your email address", html);
            return Results.Json(new { ok = true, expiresAtUtc = pending.ExpiresAt });
        })
        .WithMetadata(EndpointAccessMetadata.Private);

        group.MapGet("/confirm", async (HttpRequest req, EmailVerificationService verifications) =>
        {
            var tokenRaw = req.Query["token"].ToString();
            if (!Guid.TryParse(tokenRaw, out var token))
                return Results.BadRequest("Invalid verification token.");

            var result = await verifications.ConfirmAsync(token);
            return result switch
            {
                EmailVerificationResult.Success => Results.Text("Email verified successfully."),
                EmailVerificationResult.NotFound => Results.StatusCode(StatusCodes.Status410Gone),
                EmailVerificationResult.Expired => Results.Text("Verification link expired.", statusCode: StatusCodes.Status410Gone),
                EmailVerificationResult.EmailAlreadyTaken => Results.BadRequest("This email address is already in use by another player."),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        })
        .WithMetadata(EndpointAccessMetadata.Public);
    }

    private static bool IsLikelyEmail(string email)
    {
        try { var addr = new System.Net.Mail.MailAddress(email); return addr.Address == email; }
        catch { return false; }
    }

    private sealed record EmailVerificationRequest(string Email);
}
