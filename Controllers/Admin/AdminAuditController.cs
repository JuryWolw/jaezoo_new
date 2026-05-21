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
    public async Task<ActionResult<IReadOnlyList<AdminAuditLogDto>>> Get([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        var items = await db.AdminAuditLogs
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
