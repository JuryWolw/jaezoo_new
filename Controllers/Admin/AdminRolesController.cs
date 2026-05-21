using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

[ApiController]
[Authorize(Policy = AuthPolicies.OwnerOnly)]
[Route("api/admin/roles")]
public sealed class AdminRolesController(AppDbContext db, AdminAuditService audit) : ControllerBase
{
    [HttpGet("users/{userId:guid}")]
    public async Task<ActionResult<IReadOnlyList<UserRoleDto>>> GetUserRoles(Guid userId, CancellationToken ct)
    {
        var roles = await db.UserRoles
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.GrantedAt)
            .Select(r => new UserRoleDto(r.Id, r.UserId, r.Role, r.GrantedAt, r.GrantedByUserId, r.Reason, r.RevokedAt, r.RevokedByUserId, r.RevokeReason))
            .ToListAsync(ct);

        return roles;
    }

    [HttpPost("grant")]
    public async Task<IActionResult> Grant([FromBody] GrantUserRoleRequest request, CancellationToken ct)
    {
        if (!TryParseRole(request.Role, out var requestedRole))
            return BadRequest("Неизвестная роль.");

        if (requestedRole == GlobalRole.User)
            return BadRequest("Роль User не выдаётся вручную.");

        var target = await db.Users.FindAsync(new object?[] { request.UserId }, ct);
        if (target is null)
            return NotFound("Пользователь не найден.");

        var alreadyActive = await db.UserRoles.AnyAsync(r =>
            r.UserId == request.UserId &&
            r.Role == requestedRole &&
            r.RevokedAt == null, ct);

        if (alreadyActive)
            return Conflict("У пользователя уже есть эта активная роль.");

        var actorId = GetActorUserId();
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            Role = requestedRole,
            GrantedAt = DateTime.UtcNow,
            GrantedByUserId = actorId,
            Reason = Trim(request.Reason, 256)
        });

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "RoleGranted", "User", request.UserId.ToString(), $"Granted {requestedRole} to {target.PublicId} / {UserIdentityService.GetPublicName(target)}. Reason: {request.Reason}", ct);
        return NoContent();
    }

    [HttpPost("revoke")]
    public async Task<IActionResult> Revoke([FromBody] RevokeUserRoleRequest request, CancellationToken ct)
    {
        if (!TryParseRole(request.Role, out var requestedRole))
            return BadRequest("Неизвестная роль.");

        if (requestedRole == GlobalRole.User)
            return BadRequest("Роль User не отзывается вручную.");

        if (requestedRole == GlobalRole.Owner)
        {
            var activeOwners = await db.UserRoles.CountAsync(r => r.Role == GlobalRole.Owner && r.RevokedAt == null, ct);
            if (activeOwners <= 1)
                return BadRequest("Нельзя снять последнюю роль Owner.");
        }

        var role = await db.UserRoles
            .Where(r => r.UserId == request.UserId && r.Role == requestedRole && r.RevokedAt == null)
            .OrderByDescending(r => r.GrantedAt)
            .FirstOrDefaultAsync(ct);

        if (role is null)
            return NotFound("Активная роль не найдена.");

        var target = await db.Users.FindAsync(new object?[] { request.UserId }, ct);
        role.RevokedAt = DateTime.UtcNow;
        role.RevokedByUserId = GetActorUserId();
        role.RevokeReason = Trim(request.Reason, 256);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(User, HttpContext, "RoleRevoked", "User", request.UserId.ToString(), $"Revoked {requestedRole} from {target?.PublicId ?? request.UserId.ToString()} / {(target is null ? "unknown" : UserIdentityService.GetPublicName(target))}. Reason: {request.Reason}", ct);
        return NoContent();
    }


    private static bool TryParseRole(string? role, out GlobalRole parsed)
    {
        role = role?.Trim();
        return Enum.TryParse(role, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }

    private Guid? GetActorUserId()
    {
        var idValue = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idValue, out var id) ? id : null;
    }

    private static string? Trim(string? value, int maxLength)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
