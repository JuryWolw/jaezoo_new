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

    /// <summary>Запись от имени конкретного пользователя — когда токена ещё нет (вход персонала).</summary>
    public Task WriteForUserAsync(User user, HttpContext httpContext, string action, string targetType, string targetId, string summary, CancellationToken ct = default)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("public_id", user.PublicId ?? string.Empty),
            new Claim("display_name", JaeZoo.Server.Services.UserIdentityService.GetPublicName(user)),
        }));
        return WriteAsync(principal, httpContext, action, targetType, targetId, summary, ct);
    }

    private static string GetClientIp(HttpContext httpContext)
    {
        // Настоящий адрес уже подставлен ForwardedHeaders от доверенного прокси; X-Forwarded-For пишет сам клиент.
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
    /// <summary>Имя и ID пользователя-цели (если цель — пользователь).</summary>
    public string? TargetName { get; init; }

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
