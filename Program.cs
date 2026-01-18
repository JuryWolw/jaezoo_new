using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Services;
using JaeZoo.Server.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ---------- DB ----------
var conn = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=jaezoo.db";
var isPg = conn.Contains("Host=", StringComparison.OrdinalIgnoreCase)
           || conn.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
           || conn.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

if (isPg) builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));
else builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(conn));

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

        // Токен для SignalR из query (?access_token=...) на /hubs/chat
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
        // 400 с ProblemDetails при невалидной модели
        o.SuppressModelStateInvalidFilter = false;
    });

// Optional: Redis backplane для масштабирования SignalR (включается, если REDIS_URL задан)
var redis = Environment.GetEnvironmentVariable("REDIS_URL");

if (!string.IsNullOrWhiteSpace(redis))
{
    builder.Services.AddSignalR(o =>
    {
        // полезно для отладки на проде (можно оставить true)
        o.EnableDetailedErrors = true;
    })
        .AddStackExchangeRedis(redis);
}
else
{
    builder.Services.AddSignalR(o =>
    {
        o.EnableDetailedErrors = true;
    });
}

// presence-трекер в памяти
builder.Services.AddSingleton<IPresenceTracker, PresenceTracker>();

// ---------- Производительность ----------
builder.Services.AddResponseCompression(); // Gzip/Brotli
builder.Services.AddResponseCaching();     // кратковременный кеш для GET

// ---------- Rate limiting ----------
builder.Services.AddRateLimiter(options =>
{
    // Глобально: не более 100 запросов в минуту с одного IP
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Отдельная политика для Auth (более строгая)
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

var app = builder.Build();

// ---------- Миграция БД + самовосстановление схемы ----------
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInit");
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    await db.Database.MigrateAsync();

    // ЖЕЛЕЗОБЕТОН: если Render DB старая и миграция почему-то не добавила колонки — добавим сами.
    if (db.Database.IsNpgsql())
    {
        try
        {
            // Важно: IF NOT EXISTS есть в Postgres и безопасен при повторных стартах.
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
            // Не валим весь сервер — но логируем, чтобы было видно причину.
            logger.LogError(ex, "Failed to ensure DirectDialogs read-state columns.");
        }
    }
}

// ---------- wwwroot/avatars ----------
var env = app.Services.GetRequiredService<IWebHostEnvironment>();
var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
Directory.CreateDirectory(Path.Combine(webRoot, "avatars"));

// ---------- Проксирование ----------
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// app.UseHttpsRedirection(); // На Render HTTPS терминируется до контейнера

// ---------- Пайплайн ----------
app.UseStaticFiles();

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

// Глобальная обработка 500
app.UseExceptionHandler("/error");
app.MapGet("/error", () => Results.Problem(
    title: "Unexpected error",
    statusCode: StatusCodes.Status500InternalServerError
));

// Health-check для Render
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

// Удобный редирект на Swagger (по желанию)
app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();
