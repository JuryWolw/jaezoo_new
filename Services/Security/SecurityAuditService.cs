using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;

namespace JaeZoo.Server.Services.Security;

public sealed class SecurityAuditService(AppDbContext db, ILogger<SecurityAuditService> log)
{
    public async Task TryWriteAsync(
        ClaimsPrincipal? principal,
        HttpContext httpContext,
        string action,
        string targetType,
        string targetId,
        string summary,
        CancellationToken ct = default)
    {
        try
        {
            var entry = new AdminAuditLog
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                ActorUserId = TryGetUserId(principal),
                ActorPublicId = Trim(principal?.FindFirstValue("public_id"), 64),
                ActorDisplayName = Trim(principal?.FindFirstValue("display_name") ?? principal?.Identity?.Name, 64),
                Action = Trim(action, 80),
                TargetType = Trim(targetType, 64),
                TargetId = Trim(targetId, 128),
                Summary = Trim(Sanitize(summary), 512),
                IpAddress = Trim(GetClientIp(httpContext), 64),
                UserAgent = Trim(httpContext.Request.Headers.UserAgent.ToString(), 256)
            };

            db.AdminAuditLogs.Add(entry);
            await db.SaveChangesAsync(ct);

            log.LogInformation(
                "Security audit: {Action}. Actor={ActorUserId} Target={TargetType}:{TargetId}. {Summary}",
                entry.Action,
                entry.ActorUserId,
                entry.TargetType,
                entry.TargetId,
                entry.Summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Security audit write failed. Action={Action} TargetType={TargetType} TargetId={TargetId}", action, targetType, targetId);
        }
    }

    public static string HashTarget(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0) return "empty";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static Guid? TryGetUserId(ClaimsPrincipal? principal)
    {
        if (principal is null) return null;

        var idValue = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue("nameid");

        return Guid.TryParse(idValue, out var id) ? id : null;
    }

    private static string GetClientIp(HttpContext httpContext)
    {
        var forwarded = httpContext.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

        return httpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var s = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s;
    }

    private static string Trim(string? value, int maxLength)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
