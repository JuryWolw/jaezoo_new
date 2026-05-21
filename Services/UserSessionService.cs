using System.Security.Cryptography;
using System.Text;
using JaeZoo.Server.Models;

namespace JaeZoo.Server.Services;

public static class UserSessionService
{
    public static string NewRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }

    public static string HashRefreshToken(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return string.Empty;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
        return Convert.ToHexString(bytes);
    }

    public static string CleanHeader(string? value, int max)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length == 0) return string.Empty;
        return s.Length <= max ? s : s[..max];
    }

    public static string GetRemoteIp(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) return CleanHeader(first, 64);
        }

        return CleanHeader(http.Connection.RemoteIpAddress?.ToString(), 64);
    }

    public static bool IsActive(UserSession s, DateTime now) =>
        s.RevokedAt == null && s.ExpiresAt > now;
}
