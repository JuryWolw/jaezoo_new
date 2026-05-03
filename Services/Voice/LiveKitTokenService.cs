using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JaeZoo.Server.Models;
using JaeZoo.Server.Options;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Voice;

public sealed class LiveKitTokenService(IOptions<LiveKitOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LiveKitOptions _options = options.Value;

    public string Url => NormalizeUrl(_options.Url);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Url) &&
        !string.IsNullOrWhiteSpace(_options.ApiKey) &&
        !string.IsNullOrWhiteSpace(_options.ApiSecret);

    public string CreateJoinToken(User user, Guid groupId, Guid sessionId, string roomName)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("LiveKit is not configured. Set LiveKit:Url, LiveKit:ApiKey and LiveKit:ApiSecret.");

        var now = DateTimeOffset.UtcNow;
        var ttl = TimeSpan.FromMinutes(Math.Clamp(_options.TokenTtlMinutes, 5, 24 * 60));

        var payload = new Dictionary<string, object?>
        {
            ["iss"] = _options.ApiKey,
            ["sub"] = user.Id.ToString(),
            ["name"] = user.UserName,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-10).ToUnixTimeSeconds(),
            ["exp"] = now.Add(ttl).ToUnixTimeSeconds(),
            ["metadata"] = JsonSerializer.Serialize(new
            {
                userId = user.Id,
                userName = user.UserName,
                groupId,
                sessionId
            }, JsonOptions),
            ["video"] = new Dictionary<string, object?>
            {
                ["roomJoin"] = true,
                ["room"] = roomName,
                ["canPublish"] = true,
                ["canSubscribe"] = true,
                ["canPublishData"] = true,
                ["canUpdateOwnMetadata"] = true
            }
        };

        var headerJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        }, JsonOptions);

        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var signingInput = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson))}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.ApiSecret));
        var signature = Base64UrlEncode(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
    }

    public static string BuildGroupRoomName(Guid groupId) => $"group-{groupId:N}-voice";

    private static string NormalizeUrl(string? url)
    {
        url = (url ?? string.Empty).Trim().TrimEnd('/');
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "wss://" + url["https://".Length..];
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "ws://" + url["http://".Length..];
        return url;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
