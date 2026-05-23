using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Admin;
using JaeZoo.Server.Services.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

[ApiController]
[Authorize(Policy = AuthPolicies.ModerationAccess)]
[Route("api/admin/reports")]
public sealed class AdminReportsController(AppDbContext db, AdminAuditService audit, IEmailSender emailSender, ILogger<AdminReportsController> log) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminReportDto>>> List([FromQuery] string? status = null, [FromQuery] int limit = 250, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var query = db.ModerationReports.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.Status == status);

        var reports = await query.OrderByDescending(r => r.CreatedAt).Take(limit).ToListAsync(ct);
        return await MapReportsAsync(reports, ct);
    }

    [HttpPost("{reportId:guid}/dismiss")]
    public async Task<IActionResult> Dismiss(Guid reportId, [FromBody] AdminReportActionRequest? request, CancellationToken ct)
    {
        var report = await db.ModerationReports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null) return NotFound("Жалоба не найдена.");
        report.Status = "Dismissed";
        report.ResolvedAt = DateTime.UtcNow;
        report.ModeratorUserId = GetActorUserId();
        report.ModerationNote = request?.Note?.Trim();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "ReportDismissed", "Report", report.Id.ToString(), $"Dismissed report {report.Id}. Note: {report.ModerationNote}", ct);
        return NoContent();
    }

    [HttpPost("{reportId:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid reportId, [FromBody] AdminReportActionRequest? request, CancellationToken ct)
    {
        var report = await db.ModerationReports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null) return NotFound("Жалоба не найдена.");
        report.Status = "Resolved";
        report.ResolvedAt = DateTime.UtcNow;
        report.ModeratorUserId = GetActorUserId();
        report.ModerationNote = request?.Note?.Trim();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "ReportResolved", "Report", report.Id.ToString(), $"Resolved report {report.Id}. Note: {report.ModerationNote}", ct);
        return NoContent();
    }

    [HttpPost("{reportId:guid}/warn")]
    public async Task<IActionResult> Warn(Guid reportId, [FromBody] AdminReportActionRequest? request, CancellationToken ct)
    {
        var report = await db.ModerationReports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null) return NotFound("Жалоба не найдена.");
        var targetUserId = report.TargetUserId;
        if (!targetUserId.HasValue) return BadRequest("У этой жалобы нет пользователя-нарушителя.");
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId.Value, ct);
        if (target is null) return NotFound("Пользователь не найден.");

        var reason = string.IsNullOrWhiteSpace(request?.Note) ? $"Жалоба: {report.Reason}" : request!.Note!.Trim();
        var subject = string.IsNullOrWhiteSpace(request?.EmailSubject) ? "Предупреждение JaeZoo" : request!.EmailSubject!.Trim();
        var body = string.IsNullOrWhiteSpace(request?.EmailBody) ? BuildDefaultWarningBody(target, reason) : request!.EmailBody!.Trim();

        db.ModerationWarnings.Add(new Models.Moderation.ModerationWarning
        {
            Id = Guid.NewGuid(),
            UserId = target.Id,
            ReportId = report.Id,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = GetActorUserId(),
            Reason = reason,
            EmailSubject = subject,
            EmailBody = body
        });
        report.Status = "WarningIssued";
        report.ResolvedAt = DateTime.UtcNow;
        report.ModeratorUserId = GetActorUserId();
        report.ModerationNote = reason;
        await db.SaveChangesAsync(ct);

        if (request?.NotifyEmail != false)
            await TrySendNotificationAsync(target, subject, body, ct);

        await audit.WriteAsync(User, HttpContext, "WarningIssued", "User", target.Id.ToString(), $"Warning issued to {target.PublicId}. Reason: {reason}", ct);
        return NoContent();
    }

    [HttpPost("{reportId:guid}/ban")]
    [Authorize(Policy = AuthPolicies.AdminAccess)]
    public async Task<IActionResult> Ban(Guid reportId, [FromBody] AdminReportActionRequest? request, CancellationToken ct)
    {
        var report = await db.ModerationReports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null) return NotFound("Жалоба не найдена.");
        var targetUserId = report.TargetUserId;
        if (!targetUserId.HasValue) return BadRequest("У этой жалобы нет пользователя-нарушителя.");
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId.Value, ct);
        if (target is null) return NotFound("Пользователь не найден.");
        if (await db.UserRoles.AnyAsync(r => r.UserId == target.Id && r.Role == GlobalRole.Owner && r.RevokedAt == null, ct))
            return BadRequest("Owner нельзя забанить через жалобу.");

        var reason = string.IsNullOrWhiteSpace(request?.Note) ? $"Бан по жалобе: {report.Reason}" : request!.Note!.Trim();
        db.ModerationBans.Add(new Models.Moderation.ModerationBan
        {
            Id = Guid.NewGuid(),
            UserId = target.Id,
            Type = "Account",
            Reason = reason,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request?.ExpiresAt,
            CreatedByUserId = GetActorUserId()
        });
        target.IsDisabled = true;
        target.DisabledReason = reason;
        target.UpdatedAt = DateTime.UtcNow;
        await db.UserSessions.Where(s => s.UserId == target.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
        report.Status = "Banned";
        report.ResolvedAt = DateTime.UtcNow;
        report.ModeratorUserId = GetActorUserId();
        report.ModerationNote = reason;
        await db.SaveChangesAsync(ct);

        if (request?.NotifyEmail != false)
        {
            var subject = string.IsNullOrWhiteSpace(request?.EmailSubject) ? "Аккаунт JaeZoo заблокирован" : request!.EmailSubject!.Trim();
            var body = string.IsNullOrWhiteSpace(request?.EmailBody) ? BuildDefaultBanBody(target, reason) : request!.EmailBody!.Trim();
            await TrySendNotificationAsync(target, subject, body, ct);
        }

        await audit.WriteAsync(User, HttpContext, "UserBannedByReport", "Report", report.Id.ToString(), $"Banned {target.PublicId}. Reason: {reason}", ct);
        return NoContent();
    }

    private async Task<List<AdminReportDto>> MapReportsAsync(IReadOnlyList<Models.Moderation.ModerationReport> reports, CancellationToken ct)
    {
        var userIds = reports.SelectMany(r => new[] { r.ReporterUserId, r.TargetUserId ?? Guid.Empty }).Where(x => x != Guid.Empty).Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.PublicId, u.DisplayName, u.Login })
            .ToDictionaryAsync(u => u.Id, ct);

        var groupIds = reports.Select(r => r.TargetGroupId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var groups = await db.GroupChats.AsNoTracking()
            .Where(g => groupIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Title })
            .ToDictionaryAsync(g => g.Id, ct);

        return reports.Select(r =>
        {
            users.TryGetValue(r.ReporterUserId, out var reporter);
            users.TryGetValue(r.TargetUserId ?? Guid.Empty, out var targetUser);
            groups.TryGetValue(r.TargetGroupId ?? Guid.Empty, out var targetGroup);
            var summary = BuildSummary(r, targetUser?.PublicId, targetUser?.DisplayName, targetGroup?.Title);
            return new AdminReportDto(
                r.Id,
                r.CreatedAt,
                r.Status,
                r.TargetType,
                r.TargetId,
                reporter?.PublicId ?? string.Empty,
                UserIdentityService.GetPublicName(reporter?.DisplayName, reporter?.Login, "Удалённый пользователь"),
                r.Reason,
                summary,
                targetUser?.PublicId,
                UserIdentityService.GetPublicName(targetUser?.DisplayName, targetUser?.Login, string.Empty),
                targetGroup?.Title,
                r.TargetUserId,
                r.TargetMessageId,
                r.TargetGroupId);
        }).ToList();
    }

    private static string BuildSummary(Models.Moderation.ModerationReport r, string? targetPublicId, string? targetDisplayName, string? groupTitle)
    {
        var target = r.TargetType switch
        {
            "User" => $"Пользователь {targetDisplayName} {targetPublicId}",
            "DirectMessage" => $"Личное сообщение {r.TargetMessageId}",
            "GroupMessage" => $"Сообщение группы {groupTitle} / {r.TargetMessageId}",
            "Group" => $"Группа {groupTitle}",
            _ => r.TargetId
        };
        var details = string.IsNullOrWhiteSpace(r.Details) ? string.Empty : $" — {r.Details}";
        return $"{target}: {r.Reason}{details}".Trim();
    }

    private async Task TrySendNotificationAsync(User user, string subject, string body, CancellationToken ct)
    {
        try { await emailSender.SendAccountNotificationAsync(user, subject, body, null, ct); }
        catch (Exception ex) { log.LogWarning(ex, "Failed to send moderation email to {UserId}", user.Id); }
    }

    private static string BuildDefaultWarningBody(User user, string reason) => $"""
Здравствуйте, {UserIdentityService.GetPublicName(user)}.

Администрация JaeZoo вынесла предупреждение вашему аккаунту.
Причина: {reason}

Пожалуйста, соблюдайте правила платформы. Повторные нарушения могут привести к временной или постоянной блокировке аккаунта.
""";

    private static string BuildDefaultBanBody(User user, string reason) => $"""
Здравствуйте, {UserIdentityService.GetPublicName(user)}.

Ваш аккаунт JaeZoo был заблокирован администрацией.
Причина: {reason}

Если вы считаете блокировку ошибочной, свяжитесь с поддержкой JaeZoo.
""";

    private Guid? GetActorUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
