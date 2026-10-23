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

    /// <summary>
    /// Адрес клиента. Берётся из соединения: ForwardedHeaders уже подставил настоящий адрес от доверенного прокси
    /// (Caddy). Первое значение X-Forwarded-For читать нельзя — его пишет сам клиент, прокси лишь дописывает в конец.
    /// </summary>
    public static string GetRemoteIp(HttpContext http)
        => CleanHeader(http.Connection.RemoteIpAddress?.ToString(), 64);

    public static bool IsActive(UserSession s, DateTime now) =>
        s.RevokedAt == null && s.ExpiresAt > now;
}
