using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Admin;

public static class RoleBootstrapService
{
    public static async Task EnsureOwnerAsync(AppDbContext db, IConfiguration config, ILogger logger, CancellationToken ct = default)
    {
        if (await db.UserRoles.AnyAsync(r => r.Role == GlobalRole.Owner && r.RevokedAt == null, ct))
            return;

        var login = First(config["Security:BootstrapOwnerLogin"], Environment.GetEnvironmentVariable("JAEZOO_BOOTSTRAP_OWNER_LOGIN"));
        var email = First(config["Security:BootstrapOwnerEmail"], Environment.GetEnvironmentVariable("JAEZOO_BOOTSTRAP_OWNER_EMAIL"));
        var publicId = First(config["Security:BootstrapOwnerPublicId"], Environment.GetEnvironmentVariable("JAEZOO_BOOTSTRAP_OWNER_PUBLIC_ID"));

        if (string.IsNullOrWhiteSpace(login) && string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(publicId))
        {
            logger.LogWarning("No active Owner role exists. Set Security:BootstrapOwnerLogin, Security:BootstrapOwnerEmail or JAEZOO_BOOTSTRAP_OWNER_LOGIN once to bootstrap the first owner.");
            return;
        }

        var normalizedLogin = string.IsNullOrWhiteSpace(login) ? null : UserIdentityService.NormalizeLogin(login);
        var normalizedEmail = string.IsNullOrWhiteSpace(email) ? null : UserIdentityService.NormalizeEmail(email);
        var normalizedPublicId = publicId?.Trim().ToUpperInvariant();

        var user = await db.Users.FirstOrDefaultAsync(u =>
            (normalizedLogin != null && u.LoginNormalized == normalizedLogin) ||
            (normalizedEmail != null && u.EmailNormalized == normalizedEmail) ||
            (normalizedPublicId != null && u.PublicId.ToUpper() == normalizedPublicId), ct);

        if (user is null)
        {
            logger.LogWarning("Owner bootstrap requested, but target user was not found. Login={Login}; Email={Email}; PublicId={PublicId}", login, email, publicId);
            return;
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Role = GlobalRole.Owner,
            GrantedAt = DateTime.UtcNow,
            GrantedByUserId = null,
            Reason = "Initial owner bootstrap"
        });

        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            ActorUserId = null,
            ActorPublicId = "system",
            ActorDisplayName = "System",
            Action = "RoleBootstrapOwnerGranted",
            TargetType = "User",
            TargetId = user.Id.ToString(),
            Summary = $"Granted Owner role to {user.PublicId} / {UserIdentityService.GetPublicName(user)}",
            IpAddress = string.Empty,
            UserAgent = "RoleBootstrapService"
        });

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Bootstrapped first Owner role for user {UserId} / {PublicId}. Remove bootstrap config after this.", user.Id, user.PublicId);
    }

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
