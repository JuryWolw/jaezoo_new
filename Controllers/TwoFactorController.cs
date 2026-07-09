using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/auth/2fa")]
public sealed class TwoFactorController(
    AppDbContext db,
    SecurityAuditService securityAudit) : ControllerBase
{
    private readonly PasswordHasher<User> _hasher = new();

    [HttpGet("status")]
    public async Task<ActionResult<TwoFactorStatusDto>> Status(CancellationToken ct)
    {
        var uid = GetCurrentUserId();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (user == null) return Unauthorized();

        var remaining = await db.TwoFactorRecoveryCodes.CountAsync(c => c.UserId == uid && c.UsedAt == null, ct);
        return new TwoFactorStatusDto(user.TwoFactorEnabled, user.TwoFactorEnabledAt, remaining);
    }

    [HttpPost("setup")]
    public async Task<ActionResult<TwoFactorSetupResponse>> BeginSetup(CancellationToken ct)
    {
        var uid = GetCurrentUserId();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (user == null) return Unauthorized();
        if (user.TwoFactorEnabled)
            return Conflict("Двухфакторная защита уже включена.");

        var secret = TotpService.GenerateSecret();
        var expiresAt = DateTime.UtcNow.AddMinutes(15);
        user.TwoFactorPendingSecretEncrypted = IdentityDataProtector.ProtectSecret(secret);
        user.TwoFactorPendingSecretExpiresAt = expiresAt;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var accountLabel = !string.IsNullOrWhiteSpace(user.PublicId) ? user.PublicId : $"user-{user.Id:N}";
        var uri = TotpService.BuildOtpAuthUri("JaeZoo", accountLabel, secret);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.2FASetupStarted", "User", user.Id.ToString(), $"2FA setup started. publicId={user.PublicId}", ct);
        return new TwoFactorSetupResponse(secret, uri, expiresAt);
    }

    [HttpPost("enable")]
    public async Task<ActionResult<TwoFactorEnableResponse>> Enable([FromBody] TwoFactorEnableRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUserId();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (user == null) return Unauthorized();
        if (user.TwoFactorEnabled)
            return Conflict("Двухфакторная защита уже включена.");
        if (string.IsNullOrWhiteSpace(user.TwoFactorPendingSecretEncrypted) || user.TwoFactorPendingSecretExpiresAt < DateTime.UtcNow)
            return BadRequest("Сначала начните привязку приложения-аутентификатора.");

        var secret = IdentityDataProtector.UnprotectSecret(user.TwoFactorPendingSecretEncrypted);
        if (!TotpService.VerifyTotp(secret, request.Code))
            return BadRequest("Неверный код из приложения-аутентификатора.");

        var now = DateTime.UtcNow;
        user.TwoFactorSecretEncrypted = user.TwoFactorPendingSecretEncrypted;
        user.TwoFactorPendingSecretEncrypted = null;
        user.TwoFactorPendingSecretExpiresAt = null;
        user.TwoFactorEnabled = true;
        user.TwoFactorEnabledAt = now;
        user.TwoFactorDisabledAt = null;
        user.UpdatedAt = now;

        var oldCodes = await db.TwoFactorRecoveryCodes.Where(c => c.UserId == uid).ToListAsync(ct);
        db.TwoFactorRecoveryCodes.RemoveRange(oldCodes);

        var codes = TotpService.GenerateRecoveryCodes();
        foreach (var code in codes)
        {
            db.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCode
            {
                Id = Guid.NewGuid(),
                UserId = uid,
                CodeHash = TotpService.HashRecoveryCode(uid, code),
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.2FAEnabled", "User", user.Id.ToString(), $"2FA enabled. publicId={user.PublicId}", ct);
        return new TwoFactorEnableResponse(true, codes);
    }

    [HttpPost("disable")]
    public async Task<IActionResult> Disable([FromBody] TwoFactorDisableRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUserId();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (user == null) return Unauthorized();
        if (!user.TwoFactorEnabled)
            return NoContent();

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? string.Empty) == PasswordVerificationResult.Failed)
            return Unauthorized("Неверный пароль.");

        if (!await VerifyCodeOrRecoveryAsync(user, request.Code, consumeRecoveryCode: true, ct))
            return BadRequest("Неверный код двухфакторной защиты.");

        user.TwoFactorEnabled = false;
        user.TwoFactorSecretEncrypted = null;
        user.TwoFactorPendingSecretEncrypted = null;
        user.TwoFactorPendingSecretExpiresAt = null;
        user.TwoFactorDisabledAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;

        var codes = await db.TwoFactorRecoveryCodes.Where(c => c.UserId == uid).ToListAsync(ct);
        db.TwoFactorRecoveryCodes.RemoveRange(codes);
        await db.SaveChangesAsync(ct);

        await securityAudit.TryWriteAsync(User, HttpContext, "Security.2FADisabled", "User", user.Id.ToString(), $"2FA disabled. publicId={user.PublicId}", ct);
        return NoContent();
    }

    [HttpPost("recovery-codes/regenerate")]
    public async Task<ActionResult<TwoFactorEnableResponse>> RegenerateRecoveryCodes([FromBody] TwoFactorRegenerateRecoveryCodesRequest request, CancellationToken ct)
    {
        var uid = GetCurrentUserId();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (user == null) return Unauthorized();
        if (!user.TwoFactorEnabled)
            return BadRequest("Сначала включите двухфакторную защиту.");

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? string.Empty) == PasswordVerificationResult.Failed)
            return Unauthorized("Неверный пароль.");

        if (!await VerifyCodeOrRecoveryAsync(user, request.Code, consumeRecoveryCode: true, ct))
            return BadRequest("Неверный код двухфакторной защиты.");

        var oldCodes = await db.TwoFactorRecoveryCodes.Where(c => c.UserId == uid).ToListAsync(ct);
        db.TwoFactorRecoveryCodes.RemoveRange(oldCodes);

        var now = DateTime.UtcNow;
        var codes = TotpService.GenerateRecoveryCodes();
        foreach (var code in codes)
        {
            db.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCode
            {
                Id = Guid.NewGuid(),
                UserId = uid,
                CodeHash = TotpService.HashRecoveryCode(uid, code),
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.2FARecoveryCodesRegenerated", "User", user.Id.ToString(), $"2FA recovery codes regenerated. publicId={user.PublicId}", ct);
        return new TwoFactorEnableResponse(true, codes);
    }

    private async Task<bool> VerifyCodeOrRecoveryAsync(User user, string? code, bool consumeRecoveryCode, CancellationToken ct)
    {
        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TwoFactorSecretEncrypted))
            return false;

        var normalized = TotpService.NormalizeCode(code);
        if (normalized.Length == 6 && normalized.All(char.IsDigit))
        {
            var secret = IdentityDataProtector.UnprotectSecret(user.TwoFactorSecretEncrypted);
            if (TotpService.VerifyTotp(secret, normalized))
                return true;
        }

        var recovery = TotpService.NormalizeRecoveryCode(code);
        if (recovery.Length >= 8)
        {
            var hash = TotpService.HashRecoveryCode(user.Id, recovery);
            var row = await db.TwoFactorRecoveryCodes.FirstOrDefaultAsync(c => c.UserId == user.Id && c.CodeHash == hash && c.UsedAt == null, ct);
            if (row != null)
            {
                if (consumeRecoveryCode)
                {
                    row.UsedAt = DateTime.UtcNow;
                    row.UsedIpAddress = UserSessionService.GetRemoteIp(HttpContext);
                }
                return true;
            }
        }

        return false;
    }

    private Guid GetCurrentUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(id, out var uid) ? uid : throw new UnauthorizedAccessException();
    }
}
