using System.Security.Claims;
using System.Security.Cryptography;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/e2ee")]
[Authorize]
public sealed class E2eeKeysController(AppDbContext db, ILogger<E2eeKeysController> log) : ControllerBase
{
    private Guid MeId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? User.FindFirstValue("nameid"), out var id)
        ? id
        : Guid.Empty;

    [HttpGet("keys/{userId:guid}")]
    public async Task<ActionResult<E2eePublicKeyDto>> GetPublicKey(Guid userId, CancellationToken ct)
    {
        var key = await db.UserE2eeKeys.AsNoTracking()
            .Where(x => x.UserId == userId && !x.IsRevoked && !string.IsNullOrEmpty(x.PublicKeyBase64))
            .OrderByDescending(x => x.LastSeenAt ?? x.UpdatedAt)
            .ThenByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (key is null) return NotFound(new { message = "E2EE public key not found." });
        return Ok(ToLegacyDto(key));
    }

    [HttpGet("keys/me")]
    public async Task<ActionResult<E2eePublicKeyDto>> GetMyPublicKey(CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var key = await db.UserE2eeKeys.AsNoTracking()
            .Where(x => x.UserId == MeId && !x.IsRevoked && !string.IsNullOrEmpty(x.PublicKeyBase64))
            .OrderByDescending(x => x.LastSeenAt ?? x.UpdatedAt)
            .ThenByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (key is null) return NotFound(new { message = "E2EE public key not found." });
        return Ok(ToLegacyDto(key));
    }

    [HttpPost("keys/me")]
    public async Task<ActionResult<E2eePublicKeyDto>> UpsertMyPublicKey([FromBody] UpsertE2eePublicKeyRequest request, CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var deviceId = NormalizeDeviceId(request.DeviceId) ?? "legacy";
        var device = await UpsertDeviceInternalAsync(MeId, deviceId, request.PublicKeyBase64, request.DeviceName, request.ReplaceExisting, ct);
        return Ok(ToLegacyDto(device));
    }

    [HttpGet("devices/me")]
    public async Task<ActionResult<IReadOnlyList<E2eeDeviceKeyDto>>> GetMyDevices(CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var keys = await db.UserE2eeKeys.AsNoTracking()
            .Where(x => x.UserId == MeId)
            .OrderByDescending(x => x.LastSeenAt ?? x.UpdatedAt)
            .ThenByDescending(x => x.UpdatedAt)
            .ToListAsync(ct);
        return Ok(keys.Select(ToDeviceDto).ToList());
    }

    [HttpGet("devices/users/{userId:guid}")]
    public async Task<ActionResult<IReadOnlyList<E2eeDeviceKeyDto>>> GetUserDevices(Guid userId, CancellationToken ct)
    {
        var keys = await db.UserE2eeKeys.AsNoTracking()
            .Where(x => x.UserId == userId && !x.IsRevoked && !string.IsNullOrEmpty(x.PublicKeyBase64))
            .OrderByDescending(x => x.LastSeenAt ?? x.UpdatedAt)
            .ThenByDescending(x => x.UpdatedAt)
            .ToListAsync(ct);
        return Ok(keys.Select(ToDeviceDto).ToList());
    }

    [HttpPost("devices/me")]
    public async Task<ActionResult<E2eeDeviceKeyDto>> UpsertMyDevice([FromBody] UpsertE2eeDeviceKeyRequest request, CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var deviceId = NormalizeDeviceId(request.DeviceId);
        if (string.IsNullOrWhiteSpace(deviceId)) return BadRequest(new { message = "DeviceId is required." });
        var device = await UpsertDeviceInternalAsync(MeId, deviceId, request.PublicKeyBase64, request.DeviceName, request.ReplaceExisting, ct);
        return Ok(ToDeviceDto(device));
    }

    [HttpPost("devices/{deviceId}/revoke")]
    public async Task<IActionResult> RevokeMyDevice(string deviceId, CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var normalized = NormalizeDeviceId(deviceId);
        if (string.IsNullOrWhiteSpace(normalized)) return BadRequest(new { message = "DeviceId is required." });
        var key = await db.UserE2eeKeys.FirstOrDefaultAsync(x => x.UserId == MeId && x.DeviceId == normalized, ct);
        if (key is null) return NotFound();
        key.IsRevoked = true;
        key.RevokedAt = DateTime.UtcNow;
        key.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<UserE2eeKey> UpsertDeviceInternalAsync(Guid userId, string deviceId, string publicKeyBase64, string? deviceName, bool replaceExisting, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64)) throw new BadHttpRequestException("Public key is required.");

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(publicKeyBase64.Trim());
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportSubjectPublicKeyInfo(raw, out _);
        }
        catch
        {
            throw new BadHttpRequestException("Invalid E2EE public key.");
        }

        if (raw.Length > 4096) throw new BadHttpRequestException("Public key is too large.");

        var now = DateTime.UtcNow;
        var existing = await db.UserE2eeKeys.FirstOrDefaultAsync(x => x.UserId == userId && x.DeviceId == deviceId, ct);
        if (existing is not null && !replaceExisting)
        {
            existing.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        if (existing is null)
        {
            existing = new UserE2eeKey
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DeviceId = deviceId,
                CreatedAt = now,
                IsTrusted = true
            };
            db.UserE2eeKeys.Add(existing);
        }

        var safeName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim();
        existing.PublicKeyBase64 = Convert.ToBase64String(raw);
        existing.Algorithm = "ECDH-P256-SPKI";
        existing.Fingerprint = Fingerprint(raw);
        existing.DeviceName = safeName is null ? null : safeName[..Math.Min(128, safeName.Length)];
        existing.IsRevoked = false;
        existing.RevokedAt = null;
        existing.LastSeenAt = now;
        existing.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        log.LogInformation("E2EE device key registered. UserId={UserId} DeviceId={DeviceId} Fingerprint={Fingerprint}", userId, deviceId, existing.Fingerprint);
        return existing;
    }

    private static E2eePublicKeyDto ToLegacyDto(UserE2eeKey key) => new(
        key.UserId,
        key.PublicKeyBase64,
        key.Algorithm,
        key.Fingerprint,
        key.UpdatedAt,
        key.DeviceId,
        key.DeviceName);

    private static E2eeDeviceKeyDto ToDeviceDto(UserE2eeKey key) => new(
        key.Id,
        key.UserId,
        key.DeviceId,
        key.PublicKeyBase64,
        key.Algorithm,
        key.Fingerprint,
        key.DeviceName,
        key.IsTrusted,
        key.IsRevoked,
        key.CreatedAt,
        key.UpdatedAt,
        key.LastSeenAt);

    private static string? NormalizeDeviceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        return s.Length > 64 ? s[..64] : s;
    }

    private static string Fingerprint(byte[] raw)
    {
        var hash = SHA256.HashData(raw);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
