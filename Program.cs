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
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/chat"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddScoped<TokenService>();

// ---------- MVC + SignalR ----------
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(o =>
    {
        o.SuppressModelStateInvalidFilter = false;
    });

// Optional: Redis backplane
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

builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();

    var endpoint = cfg["ObjectStorage:Endpoint"] ?? throw new InvalidOperationException("ObjectStorage:Endpoint missing");
    var accessKey = cfg["ObjectStorage:AccessKey"] ?? throw new InvalidOperationException("ObjectStorage:AccessKey missing");
    var secretKey = cfg["ObjectStorage:SecretKey"] ?? throw new InvalidOperationException("ObjectStorage:SecretKey missing");

    var creds = new BasicAWSCredentials(accessKey, secretKey);

    var s3cfg = new AmazonS3Config
    {
        ServiceURL = endpoint,
        ForcePathStyle = true, // ВАЖНО для Backblaze B2
    };

    return new AmazonS3Client(creds, s3cfg);
});

builder.Services.AddSingleton<IObjectStorage, B2S3Storage>();





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

// ---------- wwwroot/avatars (static) + uploads (PRIVATE storage outside wwwroot) ----------
var env = app.Services.GetRequiredService<IWebHostEnvironment>();
var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
Directory.CreateDirectory(Path.Combine(webRoot, "avatars"));

// Files:StoragePath может быть абсолютным или относительным.
// Если относительный — считаем относительно ContentRoot (НЕ wwwroot).
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

// app.UseHttpsRedirection(); // Render TLS до контейнера

// ---------- Пайплайн ----------
// Статику оставляем (нужна для wwwroot/avatars и др.)
// Доп. защита: даже если кто-то по ошибке вернёт uploads в wwwroot, /uploads/* не раздаём.
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

app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();
