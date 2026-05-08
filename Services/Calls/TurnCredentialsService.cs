using System.Security.Cryptography;
using System.Text;
using JaeZoo.Server.Models.Calls;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Calls;

public sealed class TurnCredentialsService
{
    private const string PlaceholderSecret = "SET_TURN_SHARED_SECRET";

    private readonly TurnOptions _options;
    private readonly ILogger<TurnCredentialsService> _logger;

    public TurnCredentialsService(IOptions<TurnOptions> options, ILogger<TurnCredentialsService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IceConfigResponse CreateForUser(Guid userId)
    {
        EnsureConfigured();

        var ttl = Math.Max(60, _options.TtlSeconds);
        var expiresAt = DateTime.UtcNow.AddSeconds(ttl);
        var unix = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        var username = $"{unix}:{userId:N}";
        var credential = ComputePassword(username, _options.Secret);
        var urls = GetUsableUrls();

        _logger.LogInformation(
            "TURN credentials issued for user {UserId}; urls={UrlCount}; ttl={TtlSeconds}; expires at {ExpiresAtUtc}",
            userId,
            urls.Length,
            ttl,
            expiresAt);

        var ice = new IceServerDto(urls, username, credential);
        return new IceConfigResponse([ice], ttl, expiresAt);
    }

    public TurnDiagnosticsResponse GetDiagnostics()
    {
        var problems = GetConfigurationProblems().ToArray();
        var ttl = Math.Max(60, _options.TtlSeconds);
        return new TurnDiagnosticsResponse
        {
            Configured = problems.Length == 0,
            HasSecret = !string.IsNullOrWhiteSpace(_options.Secret),
            SecretLooksLikePlaceholder = LooksLikePlaceholderSecret(_options.Secret),
            TtlSeconds = ttl,
            SampleExpiresAtUtc = DateTime.UtcNow.AddSeconds(ttl),
            Realm = string.IsNullOrWhiteSpace(_options.Realm) ? "turn.jaezoo.ru" : _options.Realm.Trim(),
            Urls = GetUsableUrls(),
            Problems = problems
        };
    }

    public void EnsureConfigured()
    {
        var problems = GetConfigurationProblems().ToArray();
        if (problems.Length == 0)
            return;

        var message = "TURN is not configured correctly: " + string.Join("; ", problems);
        _logger.LogError(message);
        throw new InvalidOperationException(message);
    }

    private IEnumerable<string> GetConfigurationProblems()
    {
        if (string.IsNullOrWhiteSpace(_options.Secret))
            yield return "Turn__Secret is missing";
        else if (LooksLikePlaceholderSecret(_options.Secret))
            yield return "Turn__Secret still contains the appsettings placeholder";

        var urls = GetUsableUrls();
        if (urls.Length == 0)
            yield return "Turn__Urls is empty";

        if (_options.TtlSeconds < 60)
            yield return "Turn__TtlSeconds must be at least 60";
    }

    private static bool LooksLikePlaceholderSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return string.Equals(trimmed, PlaceholderSecret, StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);
    }

    private string[] GetUsableUrls()
    {
        var configured = _options.Urls ?? Array.Empty<string>();
        return configured
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Where(x => x.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)
                        || x.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                        || x.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ComputePassword(string username, string secret)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(username));
        return Convert.ToBase64String(hash);
    }
}
