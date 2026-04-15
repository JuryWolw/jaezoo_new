using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Services;
using JaeZoo.Server.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Threading.RateLimiting;
using Amazon.Runtime;
using Amazon.S3;
using JaeZoo.Server.Services.Storage;
using JaeZoo.Server.Services.Calls;
using JaeZoo.Server.Services.Chat;
using JaeZoo.Server.Options;
using JaeZoo.Server.Services.Launcher;

var builder = WebApplication.CreateBuilder(args);

// ---------- DB ----------
var conn = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=jaezoo.db";
var isPg = conn.Contains("Host=", StringComparison.OrdinalIgnoreCase)
           || conn.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
           || conn.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

if (isPg) builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));
else builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(conn));

// ---------- Files / multipart limits ----------
var maxUploadBytes = builder.Configuration.GetValue<long?>("Files:MaxUploadBytes") ?? (50L * 1024 * 1024);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxUploadBytes;
});

// ---------- JWT ----------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt");
var key = jwt.GetValue<string>("Key") ?? "fallback_key_change_me";
var issuer = jwt.GetValue<string>("Issuer") ?? "JaeZoo";
var audience = jwt.GetValue<string>("Audience") ?? "JaeZooClient";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
        };

        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    (path.StartsWithSegments("/hubs/chat") || path.StartsWithSegments("/hubs/calls")))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<DirectChatService>();
builder.Services.AddScoped<GroupChatService>();

// ---------- Launcher updates ----------
builder.Services.Configure<LauncherUpdatesOptions>(
    builder.Configuration.GetSection("LauncherUpdates"));
builder.Services.AddSingleton<ILauncherUpdateService, LauncherUpdateService>();

// ---------- MVC + SignalR ----------
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(o =>
    {
        o.SuppressModelStateInvalidFilter = false;
    });

var redis = Environment.GetEnvironmentVariable("REDIS_URL");
if (!string.IsNullOrWhiteSpace(redis))
{
    builder.Services.AddSignalR(o => { o.EnableDetailedErrors = true; })
        .AddStackExchangeRedis(redis);
}
else
{
    builder.Services.AddSignalR(o => { o.EnableDetailedErrors = true; });
}

builder.Services.AddSingleton<IPresenceTracker, PresenceTracker>();
builder.Services.Configure<TurnOptions>(builder.Configuration.GetSection("Turn"));
builder.Services.Configure<CallLifecycleOptions>(builder.Configuration.GetSection("Calls:Lifecycle"));
builder.Services.AddSingleton<TurnCredentialsService>();
builder.Services.AddSingleton<CallSessionService>();
builder.Services.AddSingleton<CallAuditService>();
builder.Services.AddScoped<CallHistoryService>();
builder.Services.AddHostedService<CallSessionMonitorService>();

// ---------- Производительность ----------
builder.Services.AddResponseCompression();
builder.Services.AddResponseCaching();

// ---------- Rate limiting ----------
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// ---------- Swagger ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "JaeZoo API", Version = "v1" });
});

// ---------- Object Storage (Yandex S3-compatible) ----------
var objCfg = builder.Configuration.GetSection("ObjectStorage");
var storageAccessKey = objCfg["AccessKey"];
var storageSecretKey = objCfg["SecretKey"];
var storageConfigured = !string.IsNullOrWhiteSpace(storageAccessKey)
    && !string.IsNullOrWhiteSpace(storageSecretKey)
    && !string.Equals(storageAccessKey, "YOUR_ACCESS_KEY", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(storageSecretKey, "YOUR_SECRET_KEY", StringComparison.OrdinalIgnoreCase);

if (storageConfigured)
{
    builder.Services.AddSingleton<IAmazonS3>(sp =>
    {
        var cfg = sp.GetRequiredService<IConfiguration>();

        var endpoint = cfg["ObjectStorage:Endpoint"] ?? "https://s3.yandexcloud.net";
        var accessKey = cfg["ObjectStorage:AccessKey"] ?? throw new InvalidOperationException("ObjectStorage:AccessKey missing");
        var secretKey = cfg["ObjectStorage:SecretKey"] ?? throw new InvalidOperationException("ObjectStorage:SecretKey missing");

        var creds = new BasicAWSCredentials(accessKey, secretKey);

        var s3cfg = new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = "ru-central1",
            ForcePathStyle = true
        };

        return new AmazonS3Client(creds, s3cfg);
    });

    builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
}
else
{
    builder.Logging.AddConsole();
    Console.WriteLine("[WARN] Object storage is not configured. Falling back to local file storage.");
    builder.Services.AddSingleton<IObjectStorage, LocalObjectStorage>();
}

var app = builder.Build();

// ---------- Миграция БД + самовосстановление схемы ----------
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInit");
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    await db.Database.MigrateAsync();

    if (db.Database.IsNpgsql())
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "DirectDialogs"
                    ADD COLUMN IF NOT EXISTS "LastReadAtUser1" timestamptz NOT NULL DEFAULT TIMESTAMPTZ '0001-01-01 00:00:00+00',
                    ADD COLUMN IF NOT EXISTS "LastReadMessageIdUser1" uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                    ADD COLUMN IF NOT EXISTS "LastReadAtUser2" timestamptz NOT NULL DEFAULT TIMESTAMPTZ '0001-01-01 00:00:00+00',
                    ADD COLUMN IF NOT EXISTS "LastReadMessageIdUser2" uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
                """);

            logger.LogInformation("DirectDialogs read-state columns ensured.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to ensure DirectDialogs read-state columns.");
        }
    }
}

// ---------- wwwroot/avatars ----------
var env = app.Services.GetRequiredService<IWebHostEnvironment>();
var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
Directory.CreateDirectory(Path.Combine(webRoot, "avatars"));

// legacy local uploads path (fallback only)
var storagePath = (app.Configuration.GetValue<string>("Files:StoragePath") ?? "data/uploads").Trim();
var uploadsAbs = Path.IsPathRooted(storagePath)
    ? storagePath
    : Path.Combine(env.ContentRootPath, storagePath);

Directory.CreateDirectory(uploadsAbs);

// ---------- Проксирование ----------
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// ---------- Пайплайн ----------
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.Context.Request.Path.StartsWithSegments("/uploads"))
        {
            ctx.Context.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Context.Response.ContentLength = 0;
            ctx.Context.Response.Body = Stream.Null;
        }
    }
});

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseResponseCompression();
app.UseResponseCaching();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<LastSeenMiddleware>();

app.UseExceptionHandler("/error");
app.MapGet("/error", () => Results.Problem(
    title: "Unexpected error",
    statusCode: StatusCodes.Status500InternalServerError
));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<CallsHub>("/hubs/calls");

app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();