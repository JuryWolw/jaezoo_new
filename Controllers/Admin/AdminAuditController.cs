using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

/// <summary>Журнал: по умолчанию — только действия персонала (админка и рекламное окно), без входов и обновлений токенов всех пользователей.</summary>
[ApiController]
[Authorize(Policy = AuthPolicies.ViewAdminAudit)]
[Route("api/admin/audit")]
public sealed class AdminAuditController(AppDbContext db) : ControllerBase
{
    /// <summary>Шум, который раньше забивал журнал: просмотры, а не действия.</summary>
    private static readonly string[] NoiseActions = ["AdManifestViewed"];

    /// <param name="scope">staff — действия персонала (по умолчанию), security — события безопасности, all — всё.</param>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminAuditLogDto>>> Get(
        [FromQuery] int limit = 100,
        [FromQuery] string? scope = "staff",
        [FromQuery] string? action = null,
        [FromQuery] Guid? actorUserId = null,
        [FromQuery] string? targetType = null,
        [FromQuery] string? targetId = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        var query = db.AdminAuditLogs.AsNoTracking().AsQueryable();

        switch ((scope ?? "staff").Trim().ToLowerInvariant())
        {
            case "security":
                query = query.Where(x => x.Action.StartsWith("Security."));
                break;
            case "all":
                break;
            default:
                // Действия через админку/рекламное окно и входы персонала; остальные Security.* — входы всех подряд.
                query = query.Where(x => !x.Action.StartsWith("Security.") && !NoiseActions.Contains(x.Action));
                break;
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            var a = action.Trim();
            query = query.Where(x => x.Action == a || x.Action.StartsWith(a));
        }

        if (actorUserId.HasValue)
            query = query.Where(x => x.ActorUserId == actorUserId.Value);

        if (!string.IsNullOrWhiteSpace(targetType))
        {
            var t = targetType.Trim();
            query = query.Where(x => x.TargetType == t);
        }

        if (!string.IsNullOrWhiteSpace(targetId))
        {
            var id = targetId.Trim();
            query = query.Where(x => x.TargetId == id);
        }

        if (fromUtc.HasValue)
            query = query.Where(x => x.CreatedAt >= DateTime.SpecifyKind(fromUtc.Value, DateTimeKind.Utc));

        if (toUtc.HasValue)
            query = query.Where(x => x.CreatedAt <= DateTime.SpecifyKind(toUtc.Value, DateTimeKind.Utc));

        var rows = await query.OrderByDescending(x => x.CreatedAt).Take(limit).ToListAsync(ct);

        // Цель-пользователь показывается именем и ID, а не голым GUID.
        var userIds = rows.Where(x => x.TargetType == "User" && Guid.TryParse(x.TargetId, out _)).Select(x => Guid.Parse(x.TargetId)).Distinct().ToList();
        var names = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToListAsync(ct))
                .ToDictionary(u => u.Id, u => $"{UserIdentityService.GetPublicName(u)} ({u.PublicId})");

        return rows.Select(x => AdminAuditLogDto.From(x) with
        {
            TargetName = x.TargetType == "User" && Guid.TryParse(x.TargetId, out var uid) && names.TryGetValue(uid, out var n) ? n : null
        }).ToList();
    }
}
