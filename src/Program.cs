using Npgsql;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    var envPath = Path.Combine(builder.Environment.ContentRootPath, ".env");
    DotEnvLoader.TryLoad(envPath);
    builder.Configuration.AddEnvironmentVariables();
}

// Bind settings
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));

// Caches & Services
builder.Services.AddMemoryCache(); // demo: replace with IDistributedCache/Redis in production
builder.Services.AddSingleton<TokenStore>();

var connectionString = builder.Configuration.GetConnectionString("Default");
connectionString ??= ConnectionStringHelper.TryBuildFromEnvironment(builder.Configuration);
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
app.UseMiddleware<SessionEnforcementMiddleware>();
app.MapApplicationEndpoints();
app.MapGet("/health", () => Results.Ok("ok"))
   .WithMetadata(EndpointAccessMetadata.Public);

app.Run();
