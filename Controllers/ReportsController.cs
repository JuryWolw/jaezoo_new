using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Moderation;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Authorize]
[RequireVerifiedEmail]
[Route("api/reports")]
[EnableRateLimiting("reports")]
public sealed class ReportsController(AppDbContext db, SecurityAuditService securityAudit, ILogger<ReportsController> log) : ControllerBase
{
    private Guid MeId
    {
        get
        {
            var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? User.FindFirstValue("uid");
            return Guid.TryParse(value, out var id) ? id : Guid.Empty;
        }
    }

    [HttpPost]
    public async Task<ActionResult<ReportCreatedDto>> Create([FromBody] CreateReportRequest request, CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        if (request is null) return BadRequest(new { message = "Body is required." });

        var targetType = NormalizeTargetType(request.TargetType);
        if (targetType is null) return BadRequest(new { message = "Некорректный тип жалобы." });

        var reason = (request.Reason ?? string.Empty).Trim();
        var details = (request.Details ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(reason)) return BadRequest(new { message = "Укажите причину жалобы." });
        if (reason.Length > 128) reason = reason[..128];
        if (details.Length > 2000) details = details[..2000];

        var report = new ModerationReport
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            ReporterUserId = MeId,
            TargetType = targetType,
            Reason = reason,
            Details = details,
            Status = "Open"
        };

        switch (targetType)
        {
            case "User":
                if (!request.TargetUserId.HasValue || request.TargetUserId.Value == Guid.Empty) return BadRequest(new { message = "Не указан пользователь." });
                if (request.TargetUserId.Value == MeId) return BadRequest(new { message = "Нельзя пожаловаться на самого себя." });
                if (!await db.Users.AnyAsync(u => u.Id == request.TargetUserId.Value, ct)) return NotFound(new { message = "Пользователь не найден." });
                report.TargetUserId = request.TargetUserId.Value;
                report.TargetId = request.TargetUserId.Value.ToString();
                break;

            case "DirectMessage":
                if (!request.TargetMessageId.HasValue || request.TargetMessageId.Value == Guid.Empty) return BadRequest(new { message = "Не указано сообщение." });
                var dm = await db.DirectMessages.AsNoTracking()
                    .Where(m => m.Id == request.TargetMessageId.Value)
                    .Join(db.DirectDialogs.AsNoTracking(), m => m.DialogId, d => d.Id, (m, d) => new { Message = m, Dialog = d })
                    .FirstOrDefaultAsync(ct);
                if (dm is null) return NotFound(new { message = "Сообщение не найдено." });
                if (dm.Dialog.User1Id != MeId && dm.Dialog.User2Id != MeId) return Forbid();
                report.TargetMessageId = dm.Message.Id;
                report.TargetUserId = dm.Message.SenderId;
                report.TargetId = dm.Message.Id.ToString();
                break;

            case "GroupMessage":
                if (!request.TargetMessageId.HasValue || request.TargetMessageId.Value == Guid.Empty) return BadRequest(new { message = "Не указано сообщение." });
                var gm = await db.GroupMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == request.TargetMessageId.Value, ct);
                if (gm is null) return NotFound(new { message = "Сообщение не найдено." });
                if (!await db.GroupChatMembers.AnyAsync(m => m.GroupChatId == gm.GroupChatId && m.UserId == MeId, ct)) return Forbid();
                report.TargetMessageId = gm.Id;
                report.TargetGroupId = gm.GroupChatId;
                report.TargetUserId = gm.SenderId;
                report.TargetId = gm.Id.ToString();
                break;

            case "Group":
                if (!request.TargetGroupId.HasValue || request.TargetGroupId.Value == Guid.Empty) return BadRequest(new { message = "Не указана группа." });
                if (!await db.GroupChats.AnyAsync(g => g.Id == request.TargetGroupId.Value, ct)) return NotFound(new { message = "Группа не найдена." });
                if (!await db.GroupChatMembers.AnyAsync(m => m.GroupChatId == request.TargetGroupId.Value && m.UserId == MeId, ct)) return Forbid();
                report.TargetGroupId = request.TargetGroupId.Value;
                report.TargetId = request.TargetGroupId.Value.ToString();
                break;
        }

        db.ModerationReports.Add(report);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Moderation report created. ReportId={ReportId} Reporter={ReporterId} TargetType={TargetType} TargetId={TargetId}", report.Id, MeId, report.TargetType, report.TargetId);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.ReportCreated", "Report", report.Id.ToString(), $"Report created. targetType={report.TargetType}; targetId={report.TargetId}; reason={report.Reason}", ct);
        return Ok(new ReportCreatedDto(report.Id, report.CreatedAt, report.Status));
    }

    private static string? NormalizeTargetType(string? value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Equals("user", StringComparison.OrdinalIgnoreCase)) return "User";
        if (value.Equals("direct", StringComparison.OrdinalIgnoreCase) || value.Equals("directmessage", StringComparison.OrdinalIgnoreCase)) return "DirectMessage";
        if (value.Equals("groupmessage", StringComparison.OrdinalIgnoreCase)) return "GroupMessage";
        if (value.Equals("message", StringComparison.OrdinalIgnoreCase)) return "DirectMessage";
        if (value.Equals("group", StringComparison.OrdinalIgnoreCase)) return "Group";
        return null;
    }
}
