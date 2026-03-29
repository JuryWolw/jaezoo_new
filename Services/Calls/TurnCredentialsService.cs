using System.Security.Cryptography;
using System.Text;
using JaeZoo.Server.Models.Calls;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Calls;

public sealed class TurnCredentialsService
{
    private readonly TurnOptions _options;
    private readonly ILogger<TurnCredentialsService> _logger;

    public TurnCredentialsService(IOptions<TurnOptions> options, ILogger<TurnCredentialsService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IceConfigResponse CreateForUser(Guid userId)
    {
        if (string.IsNullOrWhiteSpace(_options.Secret))
            throw new InvalidOperationException("Turn:Secret is not configured.");

        var ttl = Math.Max(60, _options.TtlSeconds);
        var expiresAt = DateTime.UtcNow.AddSeconds(ttl);
        var unix = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        var username = $"{unix}:{userId:N}";
        var credential = ComputePassword(username, _options.Secret);

        _logger.LogInformation("TURN credentials issued for user {UserId}; expires at {ExpiresAtUtc}", userId, expiresAt);

        var ice = new IceServerDto(_options.Urls, username, credential);
        return new IceConfigResponse([ice], ttl, expiresAt);
    }

    private static string ComputePassword(string username, string secret)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(username));
        return Convert.ToBase64String(hash);
    }
}
