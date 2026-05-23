using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Moderation;
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
[Route("api/admin/bans")]
public sealed class AdminBansController(AppDbContext db, AdminAuditService audit, IEmailSender emailSender, ILogger<AdminBansController> log) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminBanDto>>> List([FromQuery] bool activeOnly = false, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var now = DateTime.UtcNow;
        var query = db.ModerationBans.AsNoTracking().AsQueryable();
        if (activeOnly) query = query.Where(b => b.RevokedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now));

        var bans = await query.OrderByDescending(b => b.CreatedAt).Take(limit).ToListAsync(ct);
        var userIds = bans.Select(b => b.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.PublicId, u.DisplayName, u.EmailEncrypted, u.Email })
            .ToDictionaryAsync(u => u.Id, ct);

        return bans.Select(b =>
        {
            users.TryGetValue(b.UserId, out var u);
            return new AdminBanDto(b.Id, b.UserId, u?.PublicId ?? string.Empty, u?.DisplayName ?? "Удалённый пользователь", u is null ? string.Empty : UserIdentityService.GetEmail(new User { Id = u.Id, Email = u.Email, EmailEncrypted = u.EmailEncrypted }), b.Type, b.Reason, b.CreatedAt, b.ExpiresAt, b.RevokedAt, b.CreatedByUserId, b.RevokedByUserId, b.RevokeReason);
        }).ToList();
    }

    [HttpPost]
    public async Task<IActionResult> Ban([FromBody] AdminBanUserRequest request, CancellationToken ct)
    {
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (target is null) return NotFound("Пользователь не найден.");
        if (await db.UserRoles.AnyAsync(r => r.UserId == target.Id && r.Role == GlobalRole.Owner && r.RevokedAt == null, ct))
            return BadRequest("Owner нельзя забанить через админку.");

        var actorId = GetActorUserId();
        var ban = new ModerationBan
        {
            Id = Guid.NewGuid(),
            UserId = target.Id,
            Type = string.IsNullOrWhiteSpace(request.Type) ? "Account" : request.Type.Trim(),
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? "Бан администрацией JaeZoo." : request.Reason.Trim(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt,
            CreatedByUserId = actorId
        };
        db.ModerationBans.Add(ban);
        target.IsDisabled = true;
        target.DisabledReason = ban.Reason;
        target.UpdatedAt = DateTime.UtcNow;
        await db.UserSessions.Where(s => s.UserId == target.Id && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
        await db.SaveChangesAsync(ct);

        if (request.NotifyEmail)
        {
            var subject = string.IsNullOrWhiteSpace(request.EmailSubject) ? "Аккаунт JaeZoo заблокирован" : request.EmailSubject.Trim();
            var body = string.IsNullOrWhiteSpace(request.EmailBody)
                ? $"""
                  Здравствуйте, {UserIdentityService.GetPublicName(target)}.

                  Ваш аккаунт JaeZoo был заблокирован администрацией.
                  Причина: {ban.Reason}

                  Если вы считаете блокировку ошибочной, свяжитесь с поддержкой JaeZoo.
                  """
                : request.EmailBody.Trim();
            try { await emailSender.SendAccountNotificationAsync(target, subject, body, null, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Failed to send ban email to {UserId}", target.Id); }
        }

        if (request.ReportId.HasValue)
        {
            var report = await db.ModerationReports.FirstOrDefaultAsync(r => r.Id == request.ReportId.Value, ct);
            if (report is not null)
            {
                report.Status = "Banned";
                report.ResolvedAt = DateTime.UtcNow;
                report.ModeratorUserId = actorId;
                report.ModerationNote = ban.Reason;
                await db.SaveChangesAsync(ct);
            }
        }

        await audit.WriteAsync(User, HttpContext, "UserBanned", "User", target.Id.ToString(), $"Banned {target.PublicId} / {UserIdentityService.GetPublicName(target)}. Reason: {ban.Reason}", ct);
        return NoContent();
    }

    [HttpPost("{banId:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid banId, [FromBody] AdminRevokeBanRequest? request, CancellationToken ct)
    {
        var ban = await db.ModerationBans.FirstOrDefaultAsync(b => b.Id == banId, ct);
        if (ban is null) return NotFound("Бан не найден.");
        if (ban.RevokedAt != null) return NoContent();

        ban.RevokedAt = DateTime.UtcNow;
        ban.RevokedByUserId = GetActorUserId();
        ban.RevokeReason = request?.Reason?.Trim();

        var hasOtherActive = await db.ModerationBans.AnyAsync(b => b.Id != ban.Id && b.UserId == ban.UserId && b.RevokedAt == null && (b.ExpiresAt == null || b.ExpiresAt > DateTime.UtcNow), ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ban.UserId, ct);
        if (user is not null && !hasOtherActive)
        {
            user.IsDisabled = false;
            user.DisabledReason = null;
            user.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "BanRevoked", "Ban", ban.Id.ToString(), $"Revoked ban for user {ban.UserId}. Reason: {request?.Reason}", ct);
        return NoContent();
    }

    private Guid? GetActorUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
