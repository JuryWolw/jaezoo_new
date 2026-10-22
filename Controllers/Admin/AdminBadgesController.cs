using System.Text.RegularExpressions;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

public sealed record SaveBadgeRequest(string? Title, string? ColorHex, string? Icon, string? Description, int SortOrder);
public sealed record AdminBadgeDto(Guid Id, string Title, string ColorHex, string? Icon, string? Description, int SortOrder, int HoldersCount);
public sealed record BadgeHolderDto(Guid UserId, string Name, string PublicId, DateTime GrantedAt);
public sealed record BadgeGrantRequest(Guid UserId);

/// <summary>Плашки у ника: создание, правка, выдача и снятие (всё — в журнал аудита).</summary>
[ApiController]
[Authorize(Policy = AuthPolicies.AdminAccess)]
[Route("api/admin/badges")]
public sealed partial class AdminBadgesController(AppDbContext db, AdminAuditService audit) : ControllerBase
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorRegex();

    [HttpGet]
    public async Task<ActionResult<List<AdminBadgeDto>>> List(CancellationToken ct)
    {
        var counts = await db.UserBadges.AsNoTracking().GroupBy(b => b.BadgeId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var badges = await db.Badges.AsNoTracking().OrderBy(b => b.SortOrder).ThenBy(b => b.Title).ToListAsync(ct);
        return badges.Select(b => ToDto(b, counts.TryGetValue(b.Id, out var n) ? n : 0)).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<AdminBadgeDto>> Create([FromBody] SaveBadgeRequest request, CancellationToken ct)
    {
        var badge = new Badge();
        if (Apply(badge, request) is { } error) return BadRequest(new { error });
        db.Badges.Add(badge);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "BadgeCreated", "Badge", badge.Id.ToString(), $"Плашка «{badge.Title}» создана", ct);
        return ToDto(badge, 0);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AdminBadgeDto>> Update(Guid id, [FromBody] SaveBadgeRequest request, CancellationToken ct)
    {
        var badge = await db.Badges.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (badge is null) return NotFound();
        if (Apply(badge, request) is { } error) return BadRequest(new { error });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "BadgeEdited", "Badge", badge.Id.ToString(), $"Плашка «{badge.Title}» изменена", ct);
        return ToDto(badge, await db.UserBadges.CountAsync(b => b.BadgeId == id, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var badge = await db.Badges.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (badge is null) return NotFound();
        await db.UserBadges.Where(b => b.BadgeId == id).ExecuteDeleteAsync(ct);
        db.Badges.Remove(badge);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "BadgeDeleted", "Badge", id.ToString(), $"Плашка «{badge.Title}» удалена у всех", ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/holders")]
    public async Task<ActionResult<List<BadgeHolderDto>>> Holders(Guid id, CancellationToken ct)
    {
        var rows = await (from ub in db.UserBadges.AsNoTracking()
                          join u in db.Users.AsNoTracking() on ub.UserId equals u.Id
                          where ub.BadgeId == id
                          orderby ub.GrantedAt descending
                          select new { u, ub.GrantedAt }).Take(500).ToListAsync(ct);
        return rows.Select(r => new BadgeHolderDto(r.u.Id, UserIdentityService.GetPublicName(r.u), r.u.PublicId, r.GrantedAt)).ToList();
    }

    [HttpPost("{id:guid}/grant")]
    public async Task<IActionResult> Grant(Guid id, [FromBody] BadgeGrantRequest request, CancellationToken ct)
    {
        var badge = await db.Badges.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (badge is null) return NotFound(new { error = "Плашка не найдена." });
        if (!await db.Users.AnyAsync(u => u.Id == request.UserId, ct)) return NotFound(new { error = "Пользователь не найден." });
        if (await db.UserBadges.AnyAsync(b => b.BadgeId == id && b.UserId == request.UserId, ct)) return NoContent();
        db.UserBadges.Add(new UserBadge { BadgeId = id, UserId = request.UserId, GrantedById = ActorId(), GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "BadgeGranted", "User", request.UserId.ToString(), $"Выдана плашка «{badge.Title}»", ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, [FromBody] BadgeGrantRequest request, CancellationToken ct)
    {
        var badge = await db.Badges.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (badge is null) return NotFound(new { error = "Плашка не найдена." });
        var removed = await db.UserBadges.Where(b => b.BadgeId == id && b.UserId == request.UserId).ExecuteDeleteAsync(ct);
        if (removed > 0)
            await audit.WriteAsync(User, HttpContext, "BadgeRevoked", "User", request.UserId.ToString(), $"Снята плашка «{badge.Title}»", ct);
        return NoContent();
    }

    private static string? Apply(Badge badge, SaveBadgeRequest request)
    {
        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length is 0 or > 40) return "Название плашки — от 1 до 40 символов.";
        var color = (request.ColorHex ?? string.Empty).Trim();
        if (!ColorRegex().IsMatch(color)) return "Цвет — в виде #RRGGBB.";
        var icon = string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim();
        if (icon is { Length: > 8 }) return "Значок — один символ или эмодзи.";
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > 200 }) return "Описание — не больше 200 символов.";
        badge.Title = title;
        badge.ColorHex = color.ToUpperInvariant();
        badge.Icon = icon;
        badge.Description = description;
        badge.SortOrder = Math.Clamp(request.SortOrder, -1000, 1000);
        return null;
    }

    private Guid? ActorId()
    {
        var sub = User.FindFirst("sub")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private static AdminBadgeDto ToDto(Badge b, int holders) => new(b.Id, b.Title, b.ColorHex, b.Icon, b.Description, b.SortOrder, holders);
}
