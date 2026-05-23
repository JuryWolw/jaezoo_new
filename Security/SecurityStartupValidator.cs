using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace JaeZoo.Server.Security;

public sealed record SecurityStartupCheck(
    string Area,
    string Name,
    bool Ok,
    bool Required,
    string Message);

public sealed record SecurityStartupReport(
    bool StrictMode,
    bool Production,
    IReadOnlyList<SecurityStartupCheck> Checks)
{
    public bool Ok => Checks.All(x => x.Ok || !x.Required);
    public IReadOnlyList<SecurityStartupCheck> Failures => Checks.Where(x => !x.Ok && x.Required).ToArray();
    public IReadOnlyList<SecurityStartupCheck> Warnings => Checks.Where(x => !x.Ok && !x.Required).ToArray();
}

public static class SecurityStartupValidator
{
    private static readonly string[] JwtPlaceholders =
    [
        "SET_A_STRONG_SECRET_KEY_FOR_LOCAL_DEV_ChangeMe",
        "fallback_key_change_me",
        "change_me",
        "changeme",
        "secret",
        "jwt_secret"
    ];

    private static readonly string[] ObjectStoragePlaceholders =
    [
        "YOUR_ACCESS_KEY",
        "YOUR_SECRET_KEY",
        "SET_ACCESS_KEY",
        "SET_SECRET_KEY",
        "change_me",
        "changeme"
    ];

    private static readonly string[] TurnPlaceholders =
    [
        "SET_TURN_SHARED_SECRET",
        "TURN_SECRET",
        "change_me",
        "changeme"
    ];

    public static SecurityStartupReport Evaluate(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var production = environment.IsProduction();
        var strictMode = production || configuration.GetValue<bool>("Security:StrictStartupValidation");
        var checks = new List<SecurityStartupCheck>();

        Add(checks, "Runtime", "Strict startup validation", true, false,
            strictMode
                ? "Strict validation is active."
                : "Strict validation is disabled outside Production.");

        var jwtKey = configuration["Jwt:Key"];
        Add(checks, "JWT", "Signing key", IsStrongSecret(jwtKey, 32, JwtPlaceholders), strictMode,
            "Jwt:Key must be set through environment/secrets, must not be a placeholder, and should be at least 32 characters.");

        var messageEncryptionEnabled = configuration.GetValue<bool>("Messages:Encryption:Enabled");
        var messageEncryptionKey = FirstNotEmpty(
            configuration["Messages:Encryption:KeyBase64"],
            configuration["Messages:Encryption:Key"],
            configuration["Messages:Encryption:KeyHex"]);
        Add(checks, "Messages", "Database encryption enabled", messageEncryptionEnabled, strictMode,
            "Messages:Encryption:Enabled must be true in Production.");
        Add(checks, "Messages", "Database encryption key", IsStrongSecret(messageEncryptionKey, 32), strictMode && messageEncryptionEnabled,
            "Messages:Encryption key must be present and strong when message DB encryption is enabled.");

        var storageAccessKey = configuration["ObjectStorage:AccessKey"];
        var storageSecretKey = configuration["ObjectStorage:SecretKey"];
        Add(checks, "ObjectStorage", "Access key", IsStrongSecret(storageAccessKey, 12, ObjectStoragePlaceholders), strictMode,
            "ObjectStorage:AccessKey must be configured in Production. Local file storage fallback is allowed only for development.");
        Add(checks, "ObjectStorage", "Secret key", IsStrongSecret(storageSecretKey, 24, ObjectStoragePlaceholders), strictMode,
            "ObjectStorage:SecretKey must be configured in Production. Local file storage fallback is allowed only for development.");

        var postboxEnabled = configuration.GetValue<bool>("Postbox:Enabled");
        Add(checks, "Postbox", "Enabled", postboxEnabled, false,
            "Postbox is disabled. Email verification/notifications will not be fully functional.");
        Add(checks, "Postbox", "Username", IsStrongSecret(configuration["Postbox:UserName"], 8), strictMode && postboxEnabled,
            "Postbox:UserName is required when Postbox is enabled.");
        Add(checks, "Postbox", "Password", IsStrongSecret(configuration["Postbox:Password"], 16), strictMode && postboxEnabled,
            "Postbox:Password is required when Postbox is enabled.");
        Add(checks, "Postbox", "From email", LooksLikeEmail(configuration["Postbox:FromEmail"]), strictMode && postboxEnabled,
            "Postbox:FromEmail must be a valid sender address.");

        var captchaEnabled = configuration.GetValue<bool>("SmartCaptcha:Enabled");
        Add(checks, "SmartCaptcha", "Enabled", captchaEnabled, false,
            "SmartCaptcha is disabled. Risk actions will rely only on rate limits and server checks.");
        Add(checks, "SmartCaptcha", "Server key", IsStrongSecret(configuration["SmartCaptcha:ServerKey"], 16), strictMode && captchaEnabled,
            "SmartCaptcha:ServerKey is required when SmartCaptcha is enabled.");
        var captchaFailOpen = configuration.GetValue<bool>("SmartCaptcha:FailOpen");
        Add(checks, "SmartCaptcha", "Fail closed", !captchaFailOpen, strictMode && captchaEnabled,
            "SmartCaptcha:FailOpen must be false in Production.");

        Add(checks, "TURN", "Shared secret", IsStrongSecret(configuration["Turn:Secret"], 24, TurnPlaceholders), strictMode,
            "Turn:Secret must be a real shared secret in Production.");

        var liveKitUrl = configuration["LiveKit:Url"];
        var liveKitApiKey = configuration["LiveKit:ApiKey"];
        var liveKitApiSecret = configuration["LiveKit:ApiSecret"];
        var liveKitConfigured = !string.IsNullOrWhiteSpace(liveKitUrl) || !string.IsNullOrWhiteSpace(liveKitApiKey) || !string.IsNullOrWhiteSpace(liveKitApiSecret);
        Add(checks, "LiveKit", "URL", !liveKitConfigured || IsWsUrl(liveKitUrl), strictMode && liveKitConfigured,
            "LiveKit:Url must be ws:// or wss:// when LiveKit is configured.");
        Add(checks, "LiveKit", "API key", !liveKitConfigured || IsStrongSecret(liveKitApiKey, 6), strictMode && liveKitConfigured,
            "LiveKit:ApiKey is required when LiveKit is configured.");
        Add(checks, "LiveKit", "API secret", !liveKitConfigured || IsStrongSecret(liveKitApiSecret, 24), strictMode && liveKitConfigured,
            "LiveKit:ApiSecret is required when LiveKit is configured.");

        var swaggerEnabled = configuration.GetValue<bool>("Swagger");
        var allowSwaggerInProduction = configuration.GetValue<bool>("Security:AllowSwaggerInProduction");
        Add(checks, "Swagger", "Disabled in production", !production || !swaggerEnabled || allowSwaggerInProduction, strictMode,
            "Swagger must be disabled in Production unless Security:AllowSwaggerInProduction=true is explicitly set.");

        var detailedErrors = configuration.GetValue<bool>("SignalR:EnableDetailedErrors");
        Add(checks, "SignalR", "Detailed errors disabled in production", !production || !detailedErrors, strictMode,
            "SignalR:EnableDetailedErrors must be false in Production.");

        var allowedHosts = configuration["AllowedHosts"];
        Add(checks, "HostFiltering", "AllowedHosts is restricted", !production || !string.IsNullOrWhiteSpace(allowedHosts) && allowedHosts != "*", false,
            "AllowedHosts is '*'. Put jaezoo.ru/api hostnames here before public production exposure.");

        return new SecurityStartupReport(strictMode, production, checks);
    }

    public static SecurityStartupReport ValidateOrThrow(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var report = Evaluate(configuration, environment);
        if (report.Failures.Count > 0)
        {
            var details = string.Join(Environment.NewLine, report.Failures.Select(x => $"- [{x.Area}] {x.Name}: {x.Message}"));
            throw new InvalidOperationException("JaeZoo production security validation failed:" + Environment.NewLine + details);
        }

        return report;
    }

    private static void Add(List<SecurityStartupCheck> checks, string area, string name, bool ok, bool required, string message)
    {
        checks.Add(new SecurityStartupCheck(area, name, ok, required, ok ? "OK" : message));
    }

    private static bool IsStrongSecret(string? value, int minLength, IReadOnlyCollection<string>? placeholders = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length < minLength)
            return false;

        if (placeholders is not null && placeholders.Any(p => string.Equals(trimmed, p, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    private static string? FirstNotEmpty(params string?[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static bool LooksLikeEmail(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Contains('@', StringComparison.Ordinal)
               && value.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsWsUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && (value.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
                   || value.StartsWith("ws://", StringComparison.OrdinalIgnoreCase));
    }
}
