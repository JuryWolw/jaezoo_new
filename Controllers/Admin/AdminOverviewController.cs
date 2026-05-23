using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Files;
using JaeZoo.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

[ApiController]
[Authorize(Policy = AuthPolicies.AdminPanelAccess)]
[Route("api/admin/overview")]
public sealed class AdminOverviewController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminOverviewDto>> Get(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var day = now.AddDays(-1);
        var week = now.AddDays(-7);
        var month = now.AddMonths(-1);
        var halfYear = now.AddMonths(-6);
        var year = now.AddYears(-1);

        var totalUsers = await db.Users.CountAsync(ct);
        var disabledUsers = await db.Users.CountAsync(u => u.IsDisabled, ct);
        var verifiedUsers = await db.Users.CountAsync(u => u.EmailConfirmed, ct);
        var onlineUsers = await db.Users.CountAsync(u => u.ShowOnline && u.LastSeen > now.AddMinutes(-3), ct);

        var totalFiles = await db.ChatFiles.CountAsync(ct);
        var pendingFiles = await db.ChatFiles.CountAsync(f => f.ScanStatus == FileScanStatus.Pending || f.ScanStatus == FileScanStatus.MetadataChecked, ct);
        var blockedFiles = await db.ChatFiles.CountAsync(f => f.BlockedAt != null, ct);
        var totalStorageBytes = await db.ChatFiles.Where(f => f.DeletedAt == null && f.BlockedAt == null).SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;

        var roleRows = await db.UserRoles
            .AsNoTracking()
            .Where(r => r.RevokedAt == null)
            .GroupBy(r => r.Role)
            .Select(g => new { Role = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        var activeRoles = roleRows
            .Select(x => new AdminRoleCounterDto(x.Role.ToString(), x.Count))
            .ToList();

        return new AdminOverviewDto(
            totalUsers,
            verifiedUsers,
            disabledUsers,
            onlineUsers,
            await db.Users.CountAsync(u => u.CreatedAt >= day, ct),
            await db.Users.CountAsync(u => u.CreatedAt >= week, ct),
            await db.Users.CountAsync(u => u.CreatedAt >= month, ct),
            await db.Users.CountAsync(u => u.CreatedAt >= halfYear, ct),
            await db.Users.CountAsync(u => u.CreatedAt >= year, ct),
            await db.DirectMessages.CountAsync(ct),
            await db.GroupMessages.CountAsync(ct),
            await db.GroupChats.CountAsync(ct),
            totalFiles,
            pendingFiles,
            blockedFiles,
            totalStorageBytes,
            activeRoles,
            now);
    }
}
