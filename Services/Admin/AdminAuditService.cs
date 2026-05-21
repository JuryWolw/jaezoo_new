using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Admin;

public sealed class AdminAuditService(AppDbContext db, ILogger<AdminAuditService> log)
{
    public async Task WriteAsync(
        ClaimsPrincipal principal,
        HttpContext httpContext,
        string action,
        string targetType,
        string targetId,
        string summary,
        CancellationToken ct = default)
    {
        Guid? actorUserId = null;
        var idValue = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (Guid.TryParse(idValue, out var parsedId))
            actorUserId = parsedId;

        var entry = new AdminAuditLog
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            ActorUserId = actorUserId,
            ActorPublicId = principal.FindFirstValue("public_id") ?? string.Empty,
            ActorDisplayName = principal.FindFirstValue("display_name") ?? principal.Identity?.Name ?? string.Empty,
            Action = Trim(action, 80),
            TargetType = Trim(targetType, 64),
            TargetId = Trim(targetId, 128),
            Summary = Trim(summary, 512),
            IpAddress = Trim(GetClientIp(httpContext), 64),
            UserAgent = Trim(httpContext.Request.Headers.UserAgent.ToString(), 256)
        };

        db.AdminAuditLogs.Add(entry);
        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Admin audit: {Action}. Actor={ActorUserId} Target={TargetType}:{TargetId}. {Summary}",
            entry.Action,
            entry.ActorUserId,
            entry.TargetType,
            entry.TargetId,
            entry.Summary);
    }

    private static string GetClientIp(HttpContext httpContext)
    {
        var forwarded = httpContext.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',')[0].Trim();

        return httpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }

    private static string Trim(string? value, int maxLength)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

public sealed record AdminAuditLogDto(
    Guid Id,
    DateTime CreatedAt,
    Guid? ActorUserId,
    string ActorPublicId,
    string ActorDisplayName,
    string Action,
    string TargetType,
    string TargetId,
    string Summary,
    string IpAddress,
    string UserAgent)
{
    public static AdminAuditLogDto From(AdminAuditLog log) => new(
        log.Id,
        log.CreatedAt,
        log.ActorUserId,
        log.ActorPublicId,
        log.ActorDisplayName,
        log.Action,
        log.TargetType,
        log.TargetId,
        log.Summary,
        log.IpAddress,
        log.UserAgent);
}
