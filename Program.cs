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
using JaeZoo.Server.Services.Ads;
using JaeZoo.Server.Services.Calls;
using JaeZoo.Server.Services.Chat;
using JaeZoo.Server.Options;
using JaeZoo.Server.Services.Launcher;
using JaeZoo.Server.Services.Voice;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services.Admin;
using JaeZoo.Server.Services.Email;
using JaeZoo.Server.Services.Security;
using JaeZoo.Server.Services.Files;

var builder = WebApplication.CreateBuilder(args);

var startupSecurityReport = SecurityStartupValidator.ValidateOrThrow(builder.Configuration, builder.Environment);
foreach (var warning in startupSecurityReport.Warnings)
{
    Console.WriteLine($"[SECURITY WARN] [{warning.Area}] {warning.Name}: {warning.Message}");
}

MessageTextProtector.Configure(builder.Configuration);
IdentityDataProtector.Configure(builder.Configuration);

// ---------- DB ----------
var conn = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=jaezoo.db";
var isPg = conn.Contains("Host=", StringComparison.OrdinalIgnoreCase)
           || conn.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
           || conn.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

if (isPg) builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));
else builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(conn));

// ---------- Files / multipart limits ----------
var maxUploadBytes = builder.Configuration.GetValue<long?>("Files:MaxUploadBytes") ?? (2L * 1024 * 1024 * 1024);
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
            },
            OnTokenValidated = async context =>
            {
                var sidValue = context.Principal?.FindFirst("sid")?.Value;
                if (!Guid.TryParse(sidValue, out var sessionId))
                    return;

                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var active = await db.UserSessions.AnyAsync(s =>
                    s.Id == sessionId &&
                    s.RevokedAt == null &&
                    s.ExpiresAt > now);

                if (!active)
                    context.Fail("Session revoked or expired.");
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.OwnerOnly, policy => policy.RequireRole(AuthPolicies.OwnerRoles));
    options.AddPolicy(AuthPolicies.AdminAccess, policy => policy.RequireRole(AuthPolicies.AdminRoles));
    options.AddPolicy(AuthPolicies.ManageAds, policy => policy.RequireRole(AuthPolicies.AdsManagerRoles));
    options.AddPolicy(AuthPolicies.ViewAdminAudit, policy => policy.RequireRole(AuthPolicies.AuditViewerRoles));
    options.AddPolicy(AuthPolicies.AdminPanelAccess, policy => policy.RequireRole(AuthPolicies.AdminPanelRoles));
    options.AddPolicy(AuthPolicies.ModerationAccess, policy => policy.RequireRole(AuthPolicies.ModerationRoles));
});



// ---------- Yandex SmartCaptcha ----------
builder.Services.Configure<SmartCaptchaOptions>(builder.Configuration.GetSection("SmartCaptcha"));
builder.Services.AddHttpClient<SmartCaptchaService>();
builder.Services.AddSingleton<RiskCaptchaService>();
builder.Services.AddSingleton<FileInspectionService>();
builder.Services.AddSingleton<FileBucketRouter>();
builder.Services.Configure<FileAntivirusOptions>(builder.Configuration.GetSection("Files:Antivirus"));
builder.Services.AddSingleton<IFileAntivirusScanner, FileAntivirusScanner>();
builder.Services.AddScoped<FileCleanupService>();
builder.Services.AddScoped<FileModerationService>();
builder.Services.AddHostedService<FileScanHostedService>();

// ---------- Email / Yandex Cloud Postbox ----------
builder.Services.Configure<PostboxOptions>(builder.Configuration.GetSection("Postbox"));
builder.Services.AddScoped<IEmailSender, PostboxEmailSender>();
builder.Services.AddScoped<EmailVerificationService>();

builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<DirectChatService>();
builder.Services.AddScoped<GroupChatService>();
builder.Services.AddScoped<AdminAuditService>();
builder.Services.AddScoped<SecurityAuditService>();

// ---------- Launcher updates ----------
builder.Services.Configure<LauncherUpdatesOptions>(
    builder.Configuration.GetSection("LauncherUpdates"));
builder.Services.AddSingleton<ILauncherUpdateService, LauncherUpdateService>();

// ---------- Ads ----------
builder.Services.Configure<AdsOptions>(builder.Configuration.GetSection("Ads"));
builder.Services.AddSingleton<IAdsService, AdsService>();

// ---------- MVC + SignalR ----------
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(o =>
    {
        o.SuppressModelStateInvalidFilter = false;
    });

var redis = Environment.GetEnvironmentVariable("REDIS_URL");
var signalRDetailedErrors = builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue<bool>("SignalR:EnableDetailedErrors");
if (!string.IsNullOrWhiteSpace(redis))
{
    builder.Services.AddSignalR(o => { o.EnableDetailedErrors = signalRDetailedErrors; })
        .AddStackExchangeRedis(redis);
}
else
{
    builder.Services.AddSignalR(o => { o.EnableDetailedErrors = signalRDetailedErrors; });
}

builder.Services.AddSingleton<IPresenceTracker, PresenceTracker>();
builder.Services.Configure<TurnOptions>(builder.Configuration.GetSection("Turn"));
builder.Services.Configure<CallLifecycleOptions>(builder.Configuration.GetSection("Calls:Lifecycle"));
builder.Services.Configure<LiveKitOptions>(builder.Configuration.GetSection("LiveKit"));
builder.Services.AddSingleton<LiveKitTokenService>();
builder.Services.AddScoped<GroupVoiceService>();
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

    options.AddPolicy("chat-write", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("sub")?.Value
                          ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 25,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("file-upload", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("sub")?.Value
                          ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("friend-actions", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("sub")?.Value
                          ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("security-sensitive", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("sub")?.Value
                          ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 8,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("search", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("sub")?.Value
                          ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("reports", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("sub")?.Value
                          ?? httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
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
    if (builder.Environment.IsProduction() || builder.Configuration.GetValue<bool>("Security:StrictStartupValidation"))
    {
        throw new InvalidOperationException("Object storage is not configured. Local file storage fallback is disabled in Production/strict security mode.");
    }

    Console.WriteLine("[WARN] Object storage is not configured. Falling back to local file storage for development only.");
    builder.Services.AddSingleton<IObjectStorage, LocalObjectStorage>();
}

var app = builder.Build();

// ---------- Миграция БД + самовосстановление схемы ----------
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInit");
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    await db.Database.MigrateAsync();

    await EnsureGroupVoiceTablesAsync(db, logger);
    await EnsureEmailVerificationTablesAsync(db, logger);
    await EnsureChatFileMetadataColumnsAsync(db, logger);
    await EnsureProfileMediaSchemaAsync(db, logger);
    await EnsureModerationSchemaAsync(db, logger);
    await EnsureFileThreatSchemaAsync(db, logger);
    await EnsureMessageEncryptionSchemaAsync(db, logger);
    await EnsureIdentityPrivacySchemaAsync(db, logger);
    await EnsureE2eeKeySchemaAsync(db, logger);
    await EnsureGroupE2eeSecuritySchemaAsync(db, logger);
    await EnsurePublicGroupsSchemaAsync(db, logger);
    await EnsureUserActivitySchemaAsync(db, logger);

    await IdentityDataProtector.BackfillUsersAsync(db, logger);

    if (MessageTextProtector.Enabled && app.Configuration.GetValue<bool>("Messages:Encryption:MigrateExistingOnStartup"))
    {
        await MessageEncryptionBackfill.EncryptExistingMessagesAsync(db, logger);
    }

    await RoleBootstrapService.EnsureOwnerAsync(db, app.Configuration, logger);

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

if (app.Environment.IsProduction())
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.TryAdd("X-Content-Type-Options", "nosniff");
    headers.TryAdd("X-Frame-Options", "DENY");
    headers.TryAdd("Referrer-Policy", "no-referrer");
    headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=()");
    headers.TryAdd("Cross-Origin-Resource-Policy", "same-site");
    await next();
});

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

var swaggerEnabled = app.Environment.IsDevelopment()
    || (app.Configuration.GetValue<bool>("Swagger")
        && (!app.Environment.IsProduction() || app.Configuration.GetValue<bool>("Security:AllowSwaggerInProduction")));

if (swaggerEnabled)
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

app.MapGet("/", () => Results.Ok(new { service = "JaeZoo.Server", status = "ok", swagger = swaggerEnabled }));

app.Run();






static async Task EnsureUserActivitySchemaAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "Users"
                    ADD COLUMN IF NOT EXISTS "LastSeenVisibility" integer NOT NULL DEFAULT 1,
                    ADD COLUMN IF NOT EXISTS "ShowActivity" boolean NOT NULL DEFAULT true,
                    ADD COLUMN IF NOT EXISTS "CurrentActivityName" character varying(96) NULL,
                    ADD COLUMN IF NOT EXISTS "CurrentActivityUpdatedAt" timestamp with time zone NULL;
                """);
        }
        else if (db.Database.IsSqlite())
        {
            var columns = new Dictionary<string, string>
            {
                ["LastSeenVisibility"] = "INTEGER NOT NULL DEFAULT 1",
                ["ShowActivity"] = "INTEGER NOT NULL DEFAULT 1",
                ["CurrentActivityName"] = "TEXT NULL",
                ["CurrentActivityUpdatedAt"] = "TEXT NULL"
            };

            foreach (var (name, definition) in columns)
            {
                var existingSql = "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Users') WHERE name = '" + name + "'";
                var existing = await db.Database.SqlQueryRaw<int>(existingSql).SingleAsync();
                if (existing == 0)
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" ADD COLUMN \"" + name + "\" " + definition + ";");
            }
        }

        logger.LogInformation("User activity schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure user activity schema.");
        throw;
    }
}

static async Task EnsureIdentityPrivacySchemaAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "Users"
                    ADD COLUMN IF NOT EXISTS "LoginHash" character varying(128) NOT NULL DEFAULT '',
                    ADD COLUMN IF NOT EXISTS "LoginEncrypted" character varying(1024) NOT NULL DEFAULT '',
                    ADD COLUMN IF NOT EXISTS "EmailHash" character varying(128) NOT NULL DEFAULT '',
                    ADD COLUMN IF NOT EXISTS "EmailEncrypted" character varying(1024) NOT NULL DEFAULT '',
                    ADD COLUMN IF NOT EXISTS "IdentityPrivacyVersion" integer NOT NULL DEFAULT 0;

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_LoginHash" ON "Users" ("LoginHash") WHERE "LoginHash" <> '';
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_EmailHash" ON "Users" ("EmailHash") WHERE "EmailHash" <> '';
                """);
        }
        else if (db.Database.IsSqlite())
        {
            var columns = new Dictionary<string, string>
            {
                ["LoginHash"] = "TEXT NOT NULL DEFAULT ''",
                ["LoginEncrypted"] = "TEXT NOT NULL DEFAULT ''",
                ["EmailHash"] = "TEXT NOT NULL DEFAULT ''",
                ["EmailEncrypted"] = "TEXT NOT NULL DEFAULT ''",
                ["IdentityPrivacyVersion"] = "INTEGER NOT NULL DEFAULT 0"
            };

            foreach (var (name, definition) in columns)
            {
                var existingSql = "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Users') WHERE name = '" + name + "'";
                var existing = await db.Database.SqlQueryRaw<int>(existingSql).SingleAsync();
                if (existing == 0)
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" ADD COLUMN \"" + name + "\" " + definition + ";");
            }

            await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_LoginHash\" ON \"Users\" (\"LoginHash\") WHERE \"LoginHash\" <> ''; ");
            await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_EmailHash\" ON \"Users\" (\"EmailHash\") WHERE \"EmailHash\" <> ''; ");
        }

        logger.LogInformation("Identity privacy schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure identity privacy schema.");
        throw;
    }
}

static async Task EnsureE2eeKeySchemaAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "UserE2eeKeys" (
                    "Id" uuid NOT NULL,
                    "UserId" uuid NOT NULL,
                    "DeviceId" character varying(64) NOT NULL DEFAULT 'legacy',
                    "PublicKeyBase64" character varying(8192) NOT NULL,
                    "Algorithm" character varying(64) NOT NULL,
                    "Fingerprint" character varying(128) NOT NULL,
                    "DeviceName" character varying(128) NULL,
                    "IsRevoked" boolean NOT NULL DEFAULT false,
                    "IsTrusted" boolean NOT NULL DEFAULT true,
                    "RevokedAt" timestamp with time zone NULL,
                    "LastSeenAt" timestamp with time zone NULL,
                    "DeviceKeyVersion" integer NOT NULL DEFAULT 2,
                    "TrustState" integer NOT NULL DEFAULT 1,
                    "RequiresUserVerification" boolean NOT NULL DEFAULT false,
                    "UserVerifiedAt" timestamp with time zone NULL,
                    "LastIpAddress" character varying(64) NULL,
                    "Platform" character varying(64) NULL,
                    "ClientVersion" character varying(32) NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "UpdatedAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_UserE2eeKeys" PRIMARY KEY ("Id")
                );

                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "DeviceId" character varying(64) NOT NULL DEFAULT 'legacy';
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "IsRevoked" boolean NOT NULL DEFAULT false;
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "IsTrusted" boolean NOT NULL DEFAULT true;
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "RevokedAt" timestamp with time zone NULL;
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "LastSeenAt" timestamp with time zone NULL;
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "DeviceKeyVersion" integer NOT NULL DEFAULT 2;
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "TrustState" integer NOT NULL DEFAULT 1;
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "RequiresUserVerification" boolean NOT NULL DEFAULT false;
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "UserVerifiedAt" timestamp with time zone NULL;
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "LastIpAddress" character varying(64) NULL;
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "Platform" character varying(64) NULL;
                ALTER TABLE "UserE2eeKeys" ADD COLUMN IF NOT EXISTS "ClientVersion" character varying(32) NULL;

                UPDATE "UserE2eeKeys" SET "DeviceId" = 'legacy' WHERE "DeviceId" IS NULL OR "DeviceId" = '';

                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint WHERE conname = 'FK_UserE2eeKeys_Users_UserId'
                    ) THEN
                        ALTER TABLE "UserE2eeKeys"
                            ADD CONSTRAINT "FK_UserE2eeKeys_Users_UserId"
                            FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE;
                    END IF;
                END $$;

                DROP INDEX IF EXISTS "IX_UserE2eeKeys_UserId";
                CREATE INDEX IF NOT EXISTS "IX_UserE2eeKeys_UserId" ON "UserE2eeKeys" ("UserId");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserE2eeKeys_UserId_DeviceId" ON "UserE2eeKeys" ("UserId", "DeviceId");
                CREATE INDEX IF NOT EXISTS "IX_UserE2eeKeys_Fingerprint" ON "UserE2eeKeys" ("Fingerprint");
                """);
        }
        else if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "UserE2eeKeys" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_UserE2eeKeys" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "DeviceId" TEXT NOT NULL DEFAULT 'legacy',
                    "PublicKeyBase64" TEXT NOT NULL,
                    "Algorithm" TEXT NOT NULL,
                    "Fingerprint" TEXT NOT NULL,
                    "DeviceName" TEXT NULL,
                    "IsRevoked" INTEGER NOT NULL DEFAULT 0,
                    "IsTrusted" INTEGER NOT NULL DEFAULT 1,
                    "RevokedAt" TEXT NULL,
                    "LastSeenAt" TEXT NULL,
                    "DeviceKeyVersion" INTEGER NOT NULL DEFAULT 2,
                    "TrustState" INTEGER NOT NULL DEFAULT 1,
                    "RequiresUserVerification" INTEGER NOT NULL DEFAULT 0,
                    "UserVerifiedAt" TEXT NULL,
                    "LastIpAddress" TEXT NULL,
                    "Platform" TEXT NULL,
                    "ClientVersion" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_UserE2eeKeys_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS "IX_UserE2eeKeys_UserId" ON "UserE2eeKeys" ("UserId");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserE2eeKeys_UserId_DeviceId" ON "UserE2eeKeys" ("UserId", "DeviceId");
                CREATE INDEX IF NOT EXISTS "IX_UserE2eeKeys_Fingerprint" ON "UserE2eeKeys" ("Fingerprint");
                """);

            var deviceColumns = new Dictionary<string, string>
            {
                ["DeviceKeyVersion"] = "INTEGER NOT NULL DEFAULT 2",
                ["TrustState"] = "INTEGER NOT NULL DEFAULT 1",
                ["RequiresUserVerification"] = "INTEGER NOT NULL DEFAULT 0",
                ["UserVerifiedAt"] = "TEXT NULL",
                ["LastIpAddress"] = "TEXT NULL",
                ["Platform"] = "TEXT NULL",
                ["ClientVersion"] = "TEXT NULL"
            };

            foreach (var (name, definition) in deviceColumns)
            {
                var existingSql = "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('UserE2eeKeys') WHERE name = '" + name + "'";
                var existing = await db.Database.SqlQueryRaw<int>(existingSql).SingleAsync();
                if (existing == 0)
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"UserE2eeKeys\" ADD COLUMN \"" + name + "\" " + definition + ";");
            }
        }

        logger.LogInformation("E2EE device key schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure E2EE device key schema.");
        throw;
    }
}

static async Task EnsureMessageEncryptionSchemaAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "DirectMessages"
                    ALTER COLUMN "Text" TYPE text;

                ALTER TABLE "GroupMessages"
                    ALTER COLUMN "Text" TYPE text;

                ALTER TABLE "DirectMessages"
                    ADD COLUMN IF NOT EXISTS "E2eeEnvelopeVersion" integer NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS "E2eeProtocol" character varying(64) NULL;

                ALTER TABLE "GroupMessages"
                    ADD COLUMN IF NOT EXISTS "E2eeEnvelopeVersion" integer NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS "E2eeProtocol" character varying(64) NULL;

                CREATE INDEX IF NOT EXISTS "IX_DirectMessages_E2eeEnvelopeVersion"
                    ON "DirectMessages" ("E2eeEnvelopeVersion");

                CREATE INDEX IF NOT EXISTS "IX_GroupMessages_E2eeEnvelopeVersion"
                    ON "GroupMessages" ("E2eeEnvelopeVersion");
                """);
        }
        else if (db.Database.IsSqlite())
        {
            var directColumns = new Dictionary<string, string>
            {
                ["E2eeEnvelopeVersion"] = "INTEGER NOT NULL DEFAULT 0",
                ["E2eeProtocol"] = "TEXT NULL"
            };

            foreach (var (name, definition) in directColumns)
            {
                var existingSql = "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('DirectMessages') WHERE name = '" + name + "'";
                var existing = await db.Database.SqlQueryRaw<int>(existingSql).SingleAsync();
                if (existing == 0)
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"DirectMessages\" ADD COLUMN \"" + name + "\" " + definition + ";");
            }

            var groupColumns = new Dictionary<string, string>
            {
                ["E2eeEnvelopeVersion"] = "INTEGER NOT NULL DEFAULT 0",
                ["E2eeProtocol"] = "TEXT NULL"
            };

            foreach (var (name, definition) in groupColumns)
            {
                var existingSql = "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('GroupMessages') WHERE name = '" + name + "'";
                var existing = await db.Database.SqlQueryRaw<int>(existingSql).SingleAsync();
                if (existing == 0)
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"GroupMessages\" ADD COLUMN \"" + name + "\" " + definition + ";");
            }

            await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_DirectMessages_E2eeEnvelopeVersion\" ON \"DirectMessages\" (\"E2eeEnvelopeVersion\");");
            await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_GroupMessages_E2eeEnvelopeVersion\" ON \"GroupMessages\" (\"E2eeEnvelopeVersion\");");
        }

        logger.LogInformation("Message encryption schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure message encryption schema.");
        throw;
    }
}

static async Task EnsureProfileMediaSchemaAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "Users"
                    ADD COLUMN IF NOT EXISTS "ProfileBannerUrl" character varying(512) NULL,
                    ADD COLUMN IF NOT EXISTS "ProfileTextTheme" character varying(16) NULL;

                CREATE TABLE IF NOT EXISTS "UserAvatars" (
                    "Id" uuid NOT NULL,
                    "UserId" uuid NOT NULL,
                    "Bucket" character varying(128) NOT NULL,
                    "ObjectKey" character varying(512) NOT NULL,
                    "Url" character varying(512) NOT NULL,
                    "ContentType" character varying(128) NOT NULL,
                    "SizeBytes" bigint NOT NULL,
                    "IsCurrent" boolean NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "DeletedAt" timestamp with time zone NULL,
                    CONSTRAINT "PK_UserAvatars" PRIMARY KEY ("Id")
                );

                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE conname = 'FK_UserAvatars_Users_UserId'
                    ) THEN
                        ALTER TABLE "UserAvatars"
                            ADD CONSTRAINT "FK_UserAvatars_Users_UserId"
                            FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE;
                    END IF;
                END $$;

                CREATE INDEX IF NOT EXISTS "IX_UserAvatars_UserId_DeletedAt_CreatedAt"
                    ON "UserAvatars" ("UserId", "DeletedAt", "CreatedAt");

                CREATE INDEX IF NOT EXISTS "IX_UserAvatars_UserId_IsCurrent"
                    ON "UserAvatars" ("UserId", "IsCurrent");
                """);
        }
        else if (db.Database.IsSqlite())
        {
            var columns = new Dictionary<string, string>
            {
                ["ProfileBannerUrl"] = "TEXT NULL",
                ["ProfileTextTheme"] = "TEXT NULL"
            };

            foreach (var (name, definition) in columns)
            {
                var existingSql = "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Users') WHERE name = '" + name + "'";
                var existing = await db.Database.SqlQueryRaw<int>(existingSql).SingleAsync();
                if (existing == 0)
                {
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" ADD COLUMN \"" + name + "\" " + definition + ";");
                }
            }

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "UserAvatars" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_UserAvatars" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Bucket" TEXT NOT NULL,
                    "ObjectKey" TEXT NOT NULL,
                    "Url" TEXT NOT NULL,
                    "ContentType" TEXT NOT NULL,
                    "SizeBytes" INTEGER NOT NULL,
                    "IsCurrent" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "DeletedAt" TEXT NULL,
                    CONSTRAINT "FK_UserAvatars_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS "IX_UserAvatars_UserId_DeletedAt_CreatedAt"
                    ON "UserAvatars" ("UserId", "DeletedAt", "CreatedAt");

                CREATE INDEX IF NOT EXISTS "IX_UserAvatars_UserId_IsCurrent"
                    ON "UserAvatars" ("UserId", "IsCurrent");
                """);
        }

        logger.LogInformation("Profile media schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure profile media schema.");
        throw;
    }
}

static async Task EnsureChatFileMetadataColumnsAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "ChatFiles"
                    ADD COLUMN IF NOT EXISTS "SafeFileName" character varying(256) NOT NULL DEFAULT '',
                    ADD COLUMN IF NOT EXISTS "DetectedContentType" character varying(128) NOT NULL DEFAULT 'application/octet-stream',
                    ADD COLUMN IF NOT EXISTS "Bucket" character varying(128) NOT NULL DEFAULT 'jaezoo-files',
                    ADD COLUMN IF NOT EXISTS "ObjectKey" character varying(512) NOT NULL DEFAULT '',
                    ADD COLUMN IF NOT EXISTS "Sha256" character varying(64) NOT NULL DEFAULT '',
                    ADD COLUMN IF NOT EXISTS "Kind" integer NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS "ScanStatus" integer NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS "IsPotentiallyDangerous" boolean NOT NULL DEFAULT false,
                    ADD COLUMN IF NOT EXISTS "RiskNote" character varying(512) NULL,
                    ADD COLUMN IF NOT EXISTS "DeletedAt" timestamp with time zone NULL,
                    ADD COLUMN IF NOT EXISTS "BlockedAt" timestamp with time zone NULL;

                UPDATE "ChatFiles"
                SET "ObjectKey" = "StoredPath"
                WHERE "ObjectKey" = '' AND "StoredPath" <> '';

                UPDATE "ChatFiles"
                SET "SafeFileName" = "OriginalFileName"
                WHERE "SafeFileName" = '';

                UPDATE "ChatFiles"
                SET "DetectedContentType" = "ContentType"
                WHERE "DetectedContentType" = 'application/octet-stream' AND "ContentType" <> '';

                CREATE INDEX IF NOT EXISTS "IX_ChatFiles_Bucket_ObjectKey" ON "ChatFiles" ("Bucket", "ObjectKey");
                CREATE INDEX IF NOT EXISTS "IX_ChatFiles_Sha256" ON "ChatFiles" ("Sha256");
                """);
        }
        else if (db.Database.IsSqlite())
        {
            var columns = new Dictionary<string, string>
            {
                ["SafeFileName"] = "TEXT NOT NULL DEFAULT ''",
                ["DetectedContentType"] = "TEXT NOT NULL DEFAULT 'application/octet-stream'",
                ["Bucket"] = "TEXT NOT NULL DEFAULT 'jaezoo-files'",
                ["ObjectKey"] = "TEXT NOT NULL DEFAULT ''",
                ["Sha256"] = "TEXT NOT NULL DEFAULT ''",
                ["Kind"] = "INTEGER NOT NULL DEFAULT 0",
                ["ScanStatus"] = "INTEGER NOT NULL DEFAULT 0",
                ["IsPotentiallyDangerous"] = "INTEGER NOT NULL DEFAULT 0",
                ["RiskNote"] = "TEXT NULL",
                ["DeletedAt"] = "TEXT NULL",
                ["BlockedAt"] = "TEXT NULL"
            };

            foreach (var (name, definition) in columns)
            {
                var safeName = name.Replace("'", "''");
                var existingSql = "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('ChatFiles') WHERE name = '" + safeName + "'";
                var existing = await db.Database.SqlQueryRaw<int>(existingSql).SingleAsync();
                if (existing == 0)
                {
                    var alterSql = "ALTER TABLE \"ChatFiles\" ADD COLUMN \"" + name + "\" " + definition + ";";
                    await db.Database.ExecuteSqlRawAsync(alterSql);
                }
            }

            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "ChatFiles" SET "ObjectKey" = "StoredPath" WHERE "ObjectKey" = '' AND "StoredPath" <> '';
                UPDATE "ChatFiles" SET "SafeFileName" = "OriginalFileName" WHERE "SafeFileName" = '';
                UPDATE "ChatFiles" SET "DetectedContentType" = "ContentType" WHERE "DetectedContentType" = 'application/octet-stream' AND "ContentType" <> '';
                CREATE INDEX IF NOT EXISTS "IX_ChatFiles_Bucket_ObjectKey" ON "ChatFiles" ("Bucket", "ObjectKey");
                CREATE INDEX IF NOT EXISTS "IX_ChatFiles_Sha256" ON "ChatFiles" ("Sha256");
                """);
        }

        logger.LogInformation("Chat file metadata schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure chat file metadata schema.");
        throw;
    }
}

static async Task EnsureEmailVerificationTablesAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "EmailVerificationCodes" (
                    "Id" uuid NOT NULL,
                    "UserId" uuid NOT NULL,
                    "Purpose" integer NOT NULL,
                    "CodeHash" character varying(128) NOT NULL,
                    "Salt" character varying(64) NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    "ExpiresAt" timestamp with time zone NOT NULL,
                    "ConsumedAt" timestamp with time zone NULL,
                    "AttemptCount" integer NOT NULL,
                    "LastSentAt" timestamp with time zone NOT NULL,
                    "IpAddress" character varying(64) NULL,
                    "UserAgent" character varying(256) NULL,
                    CONSTRAINT "PK_EmailVerificationCodes" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_EmailVerificationCodes_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS "IX_EmailVerificationCodes_UserId_Purpose_ConsumedAt_ExpiresAt"
                    ON "EmailVerificationCodes" ("UserId", "Purpose", "ConsumedAt", "ExpiresAt");
                """);
        }
        else if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "EmailVerificationCodes" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_EmailVerificationCodes" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Purpose" INTEGER NOT NULL,
                    "CodeHash" TEXT NOT NULL,
                    "Salt" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "ConsumedAt" TEXT NULL,
                    "AttemptCount" INTEGER NOT NULL,
                    "LastSentAt" TEXT NOT NULL,
                    "IpAddress" TEXT NULL,
                    "UserAgent" TEXT NULL,
                    CONSTRAINT "FK_EmailVerificationCodes_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS "IX_EmailVerificationCodes_UserId_Purpose_ConsumedAt_ExpiresAt"
                    ON "EmailVerificationCodes" ("UserId", "Purpose", "ConsumedAt", "ExpiresAt");
                """);
        }

        logger.LogInformation("Email verification schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure email verification schema.");
        throw;
    }
}

static async Task EnsureGroupVoiceTablesAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "GroupVoiceSessions" (
                    "Id" uuid NOT NULL,
                    "GroupChatId" uuid NOT NULL,
                    "RoomName" character varying(160) NOT NULL,
                    "StartedByUserId" uuid NOT NULL,
                    "StartedAt" timestamp with time zone NOT NULL,
                    "LastActivityAt" timestamp with time zone NOT NULL,
                    "EndedAt" timestamp with time zone NULL,
                    "State" integer NOT NULL,
                    CONSTRAINT "PK_GroupVoiceSessions" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_GroupVoiceSessions_GroupChats_GroupChatId" FOREIGN KEY ("GroupChatId") REFERENCES "GroupChats" ("Id") ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS "GroupVoiceParticipants" (
                    "Id" uuid NOT NULL,
                    "SessionId" uuid NOT NULL,
                    "GroupChatId" uuid NOT NULL,
                    "UserId" uuid NOT NULL,
                    "JoinedAt" timestamp with time zone NOT NULL,
                    "LastSeenAt" timestamp with time zone NOT NULL,
                    "LeftAt" timestamp with time zone NULL,
                    "IsActive" boolean NOT NULL,
                    "ClientInfo" character varying(256) NULL,
                    CONSTRAINT "PK_GroupVoiceParticipants" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_GroupVoiceParticipants_GroupChats_GroupChatId" FOREIGN KEY ("GroupChatId") REFERENCES "GroupChats" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_GroupVoiceParticipants_GroupVoiceSessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "GroupVoiceSessions" ("Id") ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS "IX_GroupVoiceSessions_GroupChatId_State_StartedAt" ON "GroupVoiceSessions" ("GroupChatId", "State", "StartedAt");
                CREATE INDEX IF NOT EXISTS "IX_GroupVoiceParticipants_GroupChatId_IsActive_LastSeenAt" ON "GroupVoiceParticipants" ("GroupChatId", "IsActive", "LastSeenAt");
                CREATE INDEX IF NOT EXISTS "IX_GroupVoiceParticipants_GroupChatId" ON "GroupVoiceParticipants" ("GroupChatId");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_GroupVoiceParticipants_SessionId_UserId" ON "GroupVoiceParticipants" ("SessionId", "UserId");
                """);
        }
        else if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "GroupVoiceSessions" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_GroupVoiceSessions" PRIMARY KEY,
                    "GroupChatId" TEXT NOT NULL,
                    "RoomName" TEXT NOT NULL,
                    "StartedByUserId" TEXT NOT NULL,
                    "StartedAt" TEXT NOT NULL,
                    "LastActivityAt" TEXT NOT NULL,
                    "EndedAt" TEXT NULL,
                    "State" INTEGER NOT NULL,
                    CONSTRAINT "FK_GroupVoiceSessions_GroupChats_GroupChatId" FOREIGN KEY ("GroupChatId") REFERENCES "GroupChats" ("Id") ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS "GroupVoiceParticipants" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_GroupVoiceParticipants" PRIMARY KEY,
                    "SessionId" TEXT NOT NULL,
                    "GroupChatId" TEXT NOT NULL,
                    "UserId" TEXT NOT NULL,
                    "JoinedAt" TEXT NOT NULL,
                    "LastSeenAt" TEXT NOT NULL,
                    "LeftAt" TEXT NULL,
                    "IsActive" INTEGER NOT NULL,
                    "ClientInfo" TEXT NULL,
                    CONSTRAINT "FK_GroupVoiceParticipants_GroupChats_GroupChatId" FOREIGN KEY ("GroupChatId") REFERENCES "GroupChats" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_GroupVoiceParticipants_GroupVoiceSessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "GroupVoiceSessions" ("Id") ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS "IX_GroupVoiceSessions_GroupChatId_State_StartedAt" ON "GroupVoiceSessions" ("GroupChatId", "State", "StartedAt");
                CREATE INDEX IF NOT EXISTS "IX_GroupVoiceParticipants_GroupChatId_IsActive_LastSeenAt" ON "GroupVoiceParticipants" ("GroupChatId", "IsActive", "LastSeenAt");
                CREATE INDEX IF NOT EXISTS "IX_GroupVoiceParticipants_GroupChatId" ON "GroupVoiceParticipants" ("GroupChatId");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_GroupVoiceParticipants_SessionId_UserId" ON "GroupVoiceParticipants" ("SessionId", "UserId");
                """);
        }

        logger.LogInformation("Group voice schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure group voice schema.");
        throw;
    }
}





static async Task EnsurePublicGroupsSchemaAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "GroupChats" ADD COLUMN IF NOT EXISTS "IsPublic" boolean NOT NULL DEFAULT false;
                CREATE INDEX IF NOT EXISTS "IX_GroupChats_IsPublic" ON "GroupChats" ("IsPublic");
                """);
        }
        else
        {
            var columns = await db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('GroupChats')").ToListAsync();
            if (!columns.Contains("IsPublic", StringComparer.OrdinalIgnoreCase))
                await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "GroupChats" ADD COLUMN "IsPublic" INTEGER NOT NULL DEFAULT 0;""");
            await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_GroupChats_IsPublic" ON "GroupChats" ("IsPublic");""");
        }

        logger.LogInformation("Public/private groups schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure public/private groups schema.");
        throw;
    }
}

static async Task EnsureGroupE2eeSecuritySchemaAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "GroupChats"
                    ADD COLUMN IF NOT EXISTS "SecurityEpoch" integer NOT NULL DEFAULT 1,
                    ADD COLUMN IF NOT EXISTS "SecurityEpochChangedAt" timestamptz NOT NULL DEFAULT now();

                ALTER TABLE "GroupMessages"
                    ADD COLUMN IF NOT EXISTS "GroupSecurityEpoch" integer NOT NULL DEFAULT 1;

                ALTER TABLE "GroupChats"
                    ADD COLUMN IF NOT EXISTS "HistoryPolicy" integer NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS "HistoryPolicyChangedAt" timestamptz NULL;

                UPDATE "GroupChats"
                SET "SecurityEpoch" = 1
                WHERE "SecurityEpoch" IS NULL OR "SecurityEpoch" < 1;

                UPDATE "GroupMessages"
                SET "GroupSecurityEpoch" = 1
                WHERE "GroupSecurityEpoch" IS NULL OR "GroupSecurityEpoch" < 1;

                CREATE INDEX IF NOT EXISTS "IX_GroupMessages_GroupChatId_GroupSecurityEpoch_SentAt"
                    ON "GroupMessages" ("GroupChatId", "GroupSecurityEpoch", "SentAt");
                """);
        }
        else if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "GroupChats" ADD COLUMN "SecurityEpoch" INTEGER NOT NULL DEFAULT 1;
                """);
        }
    }
    catch (Exception ex) when (db.Database.IsSqlite() && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
    {
        // SQLite does not support ADD COLUMN IF NOT EXISTS on older versions.
    }

    try
    {
        if (db.Database.IsSqlite())
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE "GroupChats" ADD COLUMN "SecurityEpochChangedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00Z';
                    """);
            }
            catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }

            try
            {
                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE "GroupMessages" ADD COLUMN "GroupSecurityEpoch" INTEGER NOT NULL DEFAULT 1;
                    """);
            }
            catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }

            try
            {
                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE "GroupChats" ADD COLUMN "HistoryPolicy" INTEGER NOT NULL DEFAULT 0;
                    """);
            }
            catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }

            try
            {
                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE "GroupChats" ADD COLUMN "HistoryPolicyChangedAt" TEXT NULL;
                    """);
            }
            catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }

            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "GroupChats"
                SET "SecurityEpoch" = 1
                WHERE "SecurityEpoch" IS NULL OR "SecurityEpoch" < 1;

                UPDATE "GroupMessages"
                SET "GroupSecurityEpoch" = 1
                WHERE "GroupSecurityEpoch" IS NULL OR "GroupSecurityEpoch" < 1;

                CREATE INDEX IF NOT EXISTS "IX_GroupMessages_GroupChatId_GroupSecurityEpoch_SentAt"
                    ON "GroupMessages" ("GroupChatId", "GroupSecurityEpoch", "SentAt");
                """);
        }

        logger.LogInformation("Group E2EE security epoch schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure group E2EE security epoch schema.");
        throw;
    }
}


static async Task EnsureFileThreatSchemaAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "FileScanAllowList" (
                    "Id" uuid NOT NULL,
                    "Sha256" character varying(64) NOT NULL,
                    "Reason" character varying(512) NOT NULL,
                    "ApprovedByUserId" uuid NULL,
                    "ApprovedAt" timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT "PK_FileScanAllowList" PRIMARY KEY ("Id")
                );

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_FileScanAllowList_Sha256"
                    ON "FileScanAllowList" ("Sha256");
                """);
        }
        else if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "FileScanAllowList" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_FileScanAllowList" PRIMARY KEY,
                    "Sha256" TEXT NOT NULL,
                    "Reason" TEXT NOT NULL,
                    "ApprovedByUserId" TEXT NULL,
                    "ApprovedAt" TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_FileScanAllowList_Sha256"
                    ON "FileScanAllowList" ("Sha256");
                """);
        }

        logger.LogInformation("File threat schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure file threat schema.");
        throw;
    }
}

static async Task EnsureModerationSchemaAsync(AppDbContext db, ILogger logger)
{
    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "ModerationBans" (
                    "Id" uuid NOT NULL,
                    "UserId" uuid NOT NULL,
                    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                    "ExpiresAt" timestamptz NULL,
                    "RevokedAt" timestamptz NULL,
                    "CreatedByUserId" uuid NULL,
                    "RevokedByUserId" uuid NULL,
                    "Type" character varying(64) NOT NULL DEFAULT 'Account',
                    "Reason" character varying(512) NOT NULL DEFAULT '',
                    "RevokeReason" character varying(512) NULL,
                    CONSTRAINT "PK_ModerationBans" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_ModerationBans_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_ModerationBans_UserId_RevokedAt_ExpiresAt" ON "ModerationBans" ("UserId", "RevokedAt", "ExpiresAt");
                CREATE INDEX IF NOT EXISTS "IX_ModerationBans_CreatedAt" ON "ModerationBans" ("CreatedAt");

                CREATE TABLE IF NOT EXISTS "ModerationReports" (
                    "Id" uuid NOT NULL,
                    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                    "ReporterUserId" uuid NOT NULL,
                    "TargetUserId" uuid NULL,
                    "TargetMessageId" uuid NULL,
                    "TargetGroupId" uuid NULL,
                    "TargetType" character varying(32) NOT NULL DEFAULT 'User',
                    "TargetId" character varying(128) NOT NULL DEFAULT '',
                    "Reason" character varying(128) NOT NULL DEFAULT '',
                    "Details" character varying(2000) NOT NULL DEFAULT '',
                    "Status" character varying(32) NOT NULL DEFAULT 'Open',
                    "ModeratorUserId" uuid NULL,
                    "ResolvedAt" timestamptz NULL,
                    "ModerationNote" character varying(2000) NULL,
                    CONSTRAINT "PK_ModerationReports" PRIMARY KEY ("Id")
                );
                CREATE INDEX IF NOT EXISTS "IX_ModerationReports_Status_CreatedAt" ON "ModerationReports" ("Status", "CreatedAt");
                CREATE INDEX IF NOT EXISTS "IX_ModerationReports_TargetUserId" ON "ModerationReports" ("TargetUserId");
                CREATE INDEX IF NOT EXISTS "IX_ModerationReports_TargetMessageId" ON "ModerationReports" ("TargetMessageId");
                CREATE INDEX IF NOT EXISTS "IX_ModerationReports_TargetGroupId" ON "ModerationReports" ("TargetGroupId");

                CREATE TABLE IF NOT EXISTS "ModerationWarnings" (
                    "Id" uuid NOT NULL,
                    "UserId" uuid NOT NULL,
                    "ReportId" uuid NULL,
                    "CreatedByUserId" uuid NULL,
                    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                    "Reason" character varying(512) NOT NULL DEFAULT '',
                    "EmailSubject" character varying(160) NOT NULL DEFAULT '',
                    "EmailBody" character varying(4000) NOT NULL DEFAULT '',
                    CONSTRAINT "PK_ModerationWarnings" PRIMARY KEY ("Id")
                );
                CREATE INDEX IF NOT EXISTS "IX_ModerationWarnings_UserId_CreatedAt" ON "ModerationWarnings" ("UserId", "CreatedAt");
                """);
        }
        else if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "ModerationBans" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ModerationBans" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "ExpiresAt" TEXT NULL,
                    "RevokedAt" TEXT NULL,
                    "CreatedByUserId" TEXT NULL,
                    "RevokedByUserId" TEXT NULL,
                    "Type" TEXT NOT NULL DEFAULT 'Account',
                    "Reason" TEXT NOT NULL DEFAULT '',
                    "RevokeReason" TEXT NULL,
                    CONSTRAINT "FK_ModerationBans_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS "IX_ModerationBans_UserId_RevokedAt_ExpiresAt" ON "ModerationBans" ("UserId", "RevokedAt", "ExpiresAt");
                CREATE INDEX IF NOT EXISTS "IX_ModerationBans_CreatedAt" ON "ModerationBans" ("CreatedAt");

                CREATE TABLE IF NOT EXISTS "ModerationReports" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ModerationReports" PRIMARY KEY,
                    "CreatedAt" TEXT NOT NULL,
                    "ReporterUserId" TEXT NOT NULL,
                    "TargetUserId" TEXT NULL,
                    "TargetMessageId" TEXT NULL,
                    "TargetGroupId" TEXT NULL,
                    "TargetType" TEXT NOT NULL DEFAULT 'User',
                    "TargetId" TEXT NOT NULL DEFAULT '',
                    "Reason" TEXT NOT NULL DEFAULT '',
                    "Details" TEXT NOT NULL DEFAULT '',
                    "Status" TEXT NOT NULL DEFAULT 'Open',
                    "ModeratorUserId" TEXT NULL,
                    "ResolvedAt" TEXT NULL,
                    "ModerationNote" TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_ModerationReports_Status_CreatedAt" ON "ModerationReports" ("Status", "CreatedAt");
                CREATE INDEX IF NOT EXISTS "IX_ModerationReports_TargetUserId" ON "ModerationReports" ("TargetUserId");
                CREATE INDEX IF NOT EXISTS "IX_ModerationReports_TargetMessageId" ON "ModerationReports" ("TargetMessageId");
                CREATE INDEX IF NOT EXISTS "IX_ModerationReports_TargetGroupId" ON "ModerationReports" ("TargetGroupId");

                CREATE TABLE IF NOT EXISTS "ModerationWarnings" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_ModerationWarnings" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "ReportId" TEXT NULL,
                    "CreatedByUserId" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "Reason" TEXT NOT NULL DEFAULT '',
                    "EmailSubject" TEXT NOT NULL DEFAULT '',
                    "EmailBody" TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS "IX_ModerationWarnings_UserId_CreatedAt" ON "ModerationWarnings" ("UserId", "CreatedAt");
                """);
        }
        logger.LogInformation("Moderation schema ensured.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to ensure moderation schema.");
    }
}
