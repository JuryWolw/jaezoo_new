using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JaeZoo.Server.Services.Voice;

/// <summary>Подпись access-токенов LiveKit (JWT HS256) и общие проверки конфигурации.</summary>
public static class LiveKitJwt
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Sign(string apiSecret, IReadOnlyDictionary<string, object?> payload)
    {
        var headerJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        }, JsonOptions);

        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var signingInput = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson))}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
        var signature = Base64UrlEncode(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
    }

    public static string SerializeMetadata(object metadata) => JsonSerializer.Serialize(metadata, JsonOptions);

    public static bool LooksLikePlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Contains("SET_", StringComparison.OrdinalIgnoreCase)
            || value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || value.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
            || value.Contains("<secret", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeUrl(string? url)
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
