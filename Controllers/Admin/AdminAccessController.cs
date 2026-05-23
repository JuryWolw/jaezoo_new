using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using JaeZoo.Server.Models;

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

        var roles = await db.UserRoles.AsNoTracking()
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .Select(r => r.Role.ToString())
            .OrderBy(x => x)
            .ToListAsync(ct);

        return new AdminMeDto(user.Id, user.PublicId, user.DisplayName, user.Email, roles);
    }
}
