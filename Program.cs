using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
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

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Missing ConnectionStrings:Default configuration for PostgreSQL database.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddScoped<DeviceStore>();
builder.Services.AddScoped<SessionManager>();
builder.Services.AddScoped<GameStore>();

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
app.MapGameEndpoints();

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
    req.Cookies.TryGetValue("device_id", out var did);
    req.Cookies.TryGetValue("sess_id", out var sid);
    return Results.Json(new { playerId = pid, deviceId = did, sessId = sid });
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
    private static readonly TimeSpan DefaultSlidingTtl = TimeSpan.FromHours(8);
    private readonly NpgsqlDataSource _dataSource;

    public SessionManager(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    // Create a new session (replaces any previous one for the player)
    public async System.Threading.Tasks.Task<string> CreateOrReplaceAsync(string playerId, TimeSpan ttl)
    {
        if (!Guid.TryParse(playerId, out var playerGuid) || playerGuid == Guid.Empty)
            throw new ArgumentException("Invalid player id.", nameof(playerId));

        var sessionGuid = Guid.NewGuid();
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var revokeCmd = conn.CreateCommand())
        {
            revokeCmd.Transaction = tx;
            revokeCmd.CommandText =
                """
                UPDATE sessions
                SET revoked_at = NOW()
                WHERE player_id = @playerId
                  AND revoked_at IS NULL;
                """;
            revokeCmd.Parameters.AddWithValue("playerId", playerGuid);
            await revokeCmd.ExecuteNonQueryAsync();
        }

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText =
                """
                INSERT INTO sessions (session_id, player_id, expires_at)
                VALUES (@sessionId, @playerId, @expiresAt);
                """;
            insertCmd.Parameters.AddWithValue("sessionId", sessionGuid);
            insertCmd.Parameters.AddWithValue("playerId", playerGuid);
            insertCmd.Parameters.AddWithValue("expiresAt", DateTime.UtcNow.Add(ttl));
            await insertCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return sessionGuid.ToString();
    }

    // Validate that sessId is the active one for this player
    public async System.Threading.Tasks.Task<bool> ValidateAsync(string playerId, string sessId, bool sliding)
    {
        if (!Guid.TryParse(playerId, out var playerGuid) || playerGuid == Guid.Empty)
            return false;
        if (!Guid.TryParse(sessId, out var sessionGuid) || sessionGuid == Guid.Empty)
            return false;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT expires_at
            FROM sessions
            WHERE player_id = @playerId
              AND session_id = @sessionId
              AND revoked_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("playerId", playerGuid);
        cmd.Parameters.AddWithValue("sessionId", sessionGuid);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return false;

        var expiresAt = reader.IsDBNull(0) ? (DateTime?)null : reader.GetFieldValue<DateTime>(0);
        await reader.CloseAsync();

        if (expiresAt is DateTime exp && exp < DateTime.UtcNow)
        {
            await MarkRevokedAsync(conn, sessionGuid);
            return false;
        }

        if (sliding)
        {
            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText =
                """
                UPDATE sessions
                SET expires_at = @expiresAt
                WHERE session_id = @sessionId;
                """;
            updateCmd.Parameters.AddWithValue("expiresAt", DateTime.UtcNow.Add(DefaultSlidingTtl));
            updateCmd.Parameters.AddWithValue("sessionId", sessionGuid);
            await updateCmd.ExecuteNonQueryAsync();
        }

        return true;
    }

    // If this sessId is active for the player, revoke it
    public async System.Threading.Tasks.Task RevokeIfActiveAsync(string playerId, string sessId)
    {
        if (!Guid.TryParse(playerId, out var playerGuid) || playerGuid == Guid.Empty)
            return;
        if (!Guid.TryParse(sessId, out var sessionGuid) || sessionGuid == Guid.Empty)
            return;

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE sessions
            SET revoked_at = NOW()
            WHERE player_id = @playerId
              AND session_id = @sessionId
              AND revoked_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("playerId", playerGuid);
        cmd.Parameters.AddWithValue("sessionId", sessionGuid);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async System.Threading.Tasks.Task MarkRevokedAsync(NpgsqlConnection conn, Guid sessionId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE sessions
            SET revoked_at = NOW()
            WHERE session_id = @sessionId
              AND revoked_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync();
    }
}
