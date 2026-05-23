using JaeZoo.Server.Data;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

[ApiController]
[Authorize(Policy = AuthPolicies.ViewAdminAudit)]
[Route("api/admin/audit")]
public sealed class AdminAuditController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminAuditLogDto>>> Get(
        [FromQuery] int limit = 100,
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

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .Select(x => new AdminAuditLogDto(
                x.Id,
                x.CreatedAt,
                x.ActorUserId,
                x.ActorPublicId,
                x.ActorDisplayName,
                x.Action,
                x.TargetType,
                x.TargetId,
                x.Summary,
                x.IpAddress,
                x.UserAgent))
            .ToListAsync(ct);

        return items;
    }
}
