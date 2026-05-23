using System.Globalization;
using System.Text.RegularExpressions;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services;

public static partial class UserIdentityService
{
    public const string DefaultAvatarUrl = "/avatars/default";

    public static string NormalizeLogin(string login) => (login ?? string.Empty).Trim().ToUpperInvariant();
    public static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToUpperInvariant();

    public static string GetLogin(User user)
        => IdentityDataProtector.UnprotectLogin(user);

    public static string GetPublicName(User user)
        => string.IsNullOrWhiteSpace(user.DisplayName) ? "Пользователь JaeZoo" : user.DisplayName!.Trim();
    public static string GetPublicName(string? displayName, string? login, string fallback = "Пользователь JaeZoo")
        => !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim() : (!string.IsNullOrWhiteSpace(login) && !login.EndsWith("@privacy.jaezoo.local", StringComparison.OrdinalIgnoreCase) && !login.StartsWith("u_", StringComparison.OrdinalIgnoreCase) ? login.Trim() : fallback);

    public static string GetEmail(User user)
        => IdentityDataProtector.UnprotectEmail(user);


    public static string GetAvatarUrl(User user)
    {
        // Public clients should always load profile avatars through the server proxy.
        // Direct Object Storage/CDN links can be misconfigured per-bucket and WPF may cache them too aggressively.
        var version = user.UpdatedAt == default ? user.Id.ToString("N") : user.UpdatedAt.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"/avatars/{user.Id}?v={version}";
    }

    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");

    public static string CreateRandomDisplayName()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4);
        var value = BitConverter.ToUInt32(bytes, 0) % 900_000 + 100_000;
        return string.Create(CultureInfo.InvariantCulture, $"ZooUser-{value}");
    }

    public static async Task<string> CreateUniquePublicIdAsync(AppDbContext db, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(5);
            var part = Convert.ToHexString(bytes).ToUpperInvariant();
            var id = $"JZ-{part[..4]}-{part[4..]}";
            if (!await db.Users.AnyAsync(u => u.PublicId == id, ct))
                return id;
        }

        return $"JZ-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
    }

    public static bool IsValidLogin(string login)
        => !string.IsNullOrWhiteSpace(login) && LoginRegex().IsMatch(login.Trim());

    [GeneratedRegex("^[a-zA-Z0-9_.-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex LoginRegex();
}
