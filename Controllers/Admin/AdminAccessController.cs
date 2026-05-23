using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

[ApiController]
[Authorize(Policy = AuthPolicies.AdminPanelAccess)]
[Route("api/admin/me")]
public sealed class AdminAccessController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminMeDto>> Get(CancellationToken ct)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(idValue, out var userId)) return Unauthorized();

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Unauthorized();

        var roleValues = await db.UserRoles.AsNoTracking()
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .Select(r => r.Role)
            .ToListAsync(ct);

        var roles = roleValues
            .Select(r => r.ToString())
            .OrderBy(x => x)
            .ToArray();

        return new AdminMeDto(
            user.Id,
            user.PublicId ?? string.Empty,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.Login : user.DisplayName,
            user.Email ?? string.Empty,
            roles);
    }
}
