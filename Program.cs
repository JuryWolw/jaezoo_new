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

    await EnsureGroupVoiceTablesAsync(db, logger);
    await EnsureEmailVerificationTablesAsync(db, logger);
    await EnsureChatFileMetadataColumnsAsync(db, logger);
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
