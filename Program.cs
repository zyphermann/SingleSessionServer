using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var startTimeUtc = DateTimeOffset.UtcNow;

// Bind settings
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));

// Caches & Services
builder.Services.AddMemoryCache(); // demo: replace with IDistributedCache/Redis in production
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<SessionManager>();

// Choose one email sender:
// 1) Real SMTP:
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
// 2) Or for local debug (prints to console):
// builder.Services.AddSingleton<IEmailSender, DebugEmailSender>();

var app = builder.Build();

// --- Middleware: enforce Single Active Session on protected routes ---
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value?.ToLowerInvariant();

    // Public endpoints that do not require an active session:
    var isPublic = path is not null && (
        path.StartsWith("/device/init") ||
        path.StartsWith("/device/transfer/") ||
        path.StartsWith("/whoami") ||
        path.StartsWith("/health") ||
        path.StartsWith("/server/info") ||
        path.StartsWith("/session/login") ||
        path.StartsWith("/session/logout")  // allow logout without session (idempotent)
    );

    if (isPublic)
    {
        await next();
        return;
    }

    var sessId = ctx.Request.Cookies["sess_id"];
    var playerId = ctx.Request.Cookies["player_id"];

    if (string.IsNullOrWhiteSpace(sessId) || string.IsNullOrWhiteSpace(playerId))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("No session.");
        return;
    }

    var sm = ctx.RequestServices.GetRequiredService<SessionManager>();
    var ok = await sm.ValidateAsync(playerId, sessId, sliding: true);

    if (!ok)
    {
        // Session invalid (kicked or expired)
        EndpointHelpers.DeleteCookie(ctx.Response, "sess_id");
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("Signed out: login from another device.");
        return;
    }

    await next();
});

app.MapDeviceEndpoints();
app.MapSessionEndpoints();

// --- 6) Protected demo endpoint
app.MapGet("/api/protected/profile", (HttpRequest req) =>
{
    var pid = req.Cookies["player_id"];
    return Results.Json(new { playerId = pid, message = "You are active." });
});

// --- diagnostics ---
app.MapGet("/whoami", (HttpRequest req) =>
{
    req.Cookies.TryGetValue("player_id", out var pid);
    req.Cookies.TryGetValue("sess_id", out var sid);
    return Results.Json(new { playerId = pid, sessId = sid });
});

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
});

app.MapGet("/health", () => Results.Ok("ok"));
app.Run();

// --- Utilities & Services ---
record EmailRequest(string Email);

sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Support";
}

sealed class AppOptions
{
    public string PublicBaseUrl { get; set; } = "";
}

interface IEmailSender
{
    System.Threading.Tasks.Task SendAsync(string to, string subject, string htmlBody);
}

sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _opt;
    public SmtpEmailSender(Microsoft.Extensions.Options.IOptions<SmtpOptions> opt) => _opt = opt.Value;

    public async System.Threading.Tasks.Task SendAsync(string to, string subject, string htmlBody)
    {
        using var client = new SmtpClient(_opt.Host, _opt.Port)
        {
            EnableSsl = _opt.EnableSsl,
            Credentials = new NetworkCredential(_opt.Username, _opt.Password),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        using var msg = new MailMessage
        {
            From = new MailAddress(_opt.FromAddress, _opt.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        msg.To.Add(to);
        await client.SendMailAsync(msg);
    }
}

sealed class DebugEmailSender : IEmailSender
{
    public System.Threading.Tasks.Task SendAsync(string to, string subject, string htmlBody)
    {
        Console.WriteLine("=== DEBUG EMAIL ===");
        Console.WriteLine($"To: {to}");
        Console.WriteLine($"Subject: {subject}");
        Console.WriteLine(htmlBody);
        Console.WriteLine("===================");
        return System.Threading.Tasks.Task.CompletedTask;
    }
}

// One-time token store (hashes tokens) using IMemoryCache
sealed class TokenStore
{
    private readonly IMemoryCache _cache;
    public TokenStore(IMemoryCache cache) => _cache = cache;

    public string CreateToken(string playerId, TimeSpan ttl)
    {
        // Create URL-safe random token
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var hash = Hash(token);
        _cache.Set($"xfer:{hash}", playerId, ttl);
        return token;
    }

    public string? ConsumeToken(string token)
    {
        var hash = Hash(token);
        var key = $"xfer:{hash}";
        if (_cache.TryGetValue<string>(key, out var playerId))
        {
            _cache.Remove(key); // one-time
            return playerId;
        }
        return null;
    }

    private static string Hash(string token)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }
}

// Single Active Session manager (player -> single sess)
sealed class SessionManager
{
    private readonly IMemoryCache _cache;
    public SessionManager(IMemoryCache cache) => _cache = cache;

    // Create a new session (replaces any previous one for the player)
    public System.Threading.Tasks.Task<string> CreateOrReplaceAsync(string playerId, TimeSpan ttl)
    {
        var sessId = NewToken();
        _cache.Set($"active:{playerId}", sessId, ttl);
        _cache.Set($"sess:{sessId}", playerId, ttl);
        return System.Threading.Tasks.Task.FromResult(sessId);
    }

    // Validate that sessId is the active one for this player
    public System.Threading.Tasks.Task<bool> ValidateAsync(string playerId, string sessId, bool sliding)
    {
        var keyActive = $"active:{playerId}";
        if (_cache.TryGetValue<string>(keyActive, out var current) && current == sessId)
        {
            if (sliding)
            {
                // Renew TTL by rewriting both keys (simple sliding expiration)
                if (_cache.TryGetValue<string>($"sess:{sessId}", out var pid))
                {
                    // For IMemoryCache we need to re-set to extend TTL
                    var ttl = TimeSpan.FromHours(8);
                    _cache.Set(keyActive, sessId, ttl);
                    _cache.Set($"sess:{sessId}", pid, ttl);
                }
            }
            return System.Threading.Tasks.Task.FromResult(true);
        }
        return System.Threading.Tasks.Task.FromResult(false);
    }

    // If this sessId is active for the player, revoke it
    public System.Threading.Tasks.Task RevokeIfActiveAsync(string playerId, string sessId)
    {
        var keyActive = $"active:{playerId}";
        if (_cache.TryGetValue<string>(keyActive, out var current) && current == sessId)
        {
            _cache.Remove(keyActive);
        }
        _cache.Remove($"sess:{sessId}");
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
