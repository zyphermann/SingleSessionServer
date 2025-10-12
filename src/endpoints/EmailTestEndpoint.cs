using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;

internal static class EmailTestEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/diagnostics/email-test", async (HttpRequest req, IEmailSender sender, IConfiguration config) =>
        {
            var testRecipient = config["Diagnostics:EmailTest:Recipient"];
            if (string.IsNullOrWhiteSpace(testRecipient))
                return Results.BadRequest(new { error = "Set Diagnostics:EmailTest:Recipient before using this endpoint." });

            var subject = "SingleSessionServer Email Test";
            var body = $"<p>This is a test mail sent at {DateTime.UtcNow:u}.</p>";

            await sender.SendAsync(testRecipient, subject, body);
            return Results.Json(new { ok = true, sentAtUtc = DateTime.UtcNow, recipient = testRecipient });
        })
        .WithMetadata(EndpointAccessMetadata.Private);
    }
}
