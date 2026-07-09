using System.Security.Claims;
using System.Security.Cryptography;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Security;
using JaeZoo.Server.Services.Chat;
using JaeZoo.Server.Services.Security;
using JaeZoo.Server.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/e2ee")]
[Authorize]
public sealed class E2eeKeysController(AppDbContext db, SecurityAuditService securityAudit, DirectChatService directChat, IHubContext<ChatHub> hub, ILogger<E2eeKeysController> log) : ControllerBase
{
    private const int TrustUnknown = 0;
    private const int TrustTofu = 1;
    private const int TrustUserVerified = 2;
    private const int TrustRevoked = 3;

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
        var device = await UpsertDeviceInternalAsync(MeId, deviceId, request.PublicKeyBase64, request.DeviceName, request.ReplaceExisting, null, null, ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.E2eeKeyUpserted", "E2EEDevice", device.DeviceId, $"Legacy E2EE public key upserted. fingerprint={device.Fingerprint}; replaceExisting={request.ReplaceExisting}", ct);
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

    [HttpGet("devices/me/{deviceId}")]
    public async Task<ActionResult<E2eeDeviceKeyDto>> GetMyDevice(string deviceId, CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var normalized = NormalizeDeviceId(deviceId);
        if (string.IsNullOrWhiteSpace(normalized)) return BadRequest(new { message = "DeviceId is required." });

        var key = await db.UserE2eeKeys.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == MeId && x.DeviceId == normalized, ct);
        if (key is null) return NotFound();
        return Ok(ToDeviceDto(key));
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

        var device = await UpsertDeviceInternalAsync(MeId, deviceId, request.PublicKeyBase64, request.DeviceName, request.ReplaceExisting, request.Platform, request.ClientVersion, ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.E2eeDeviceUpserted", "E2EEDevice", device.DeviceId, $"E2EE device upserted. fingerprint={device.Fingerprint}; replaceExisting={request.ReplaceExisting}; trustState={device.TrustState}; revoked=false", ct);
        return Ok(ToDeviceDto(device));
    }

    [HttpPatch("devices/{deviceId}/name")]
    public async Task<ActionResult<E2eeDeviceKeyDto>> RenameMyDevice(string deviceId, [FromBody] RenameE2eeDeviceRequest request, CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var normalized = NormalizeDeviceId(deviceId);
        if (string.IsNullOrWhiteSpace(normalized)) return BadRequest(new { message = "DeviceId is required." });

        var key = await db.UserE2eeKeys.FirstOrDefaultAsync(x => x.UserId == MeId && x.DeviceId == normalized, ct);
        if (key is null) return NotFound();

        key.DeviceName = CleanText(request.DeviceName, 128) ?? key.DeviceName;
        key.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.E2eeDeviceRenamed", "E2EEDevice", key.DeviceId, $"E2EE device renamed. fingerprint={key.Fingerprint}", ct);
        return Ok(ToDeviceDto(key));
    }

    [HttpPost("devices/{deviceId}/verify")]
    public async Task<ActionResult<E2eeDeviceKeyDto>> VerifyMyDevice(string deviceId, CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var normalized = NormalizeDeviceId(deviceId);
        if (string.IsNullOrWhiteSpace(normalized)) return BadRequest(new { message = "DeviceId is required." });

        var key = await db.UserE2eeKeys.FirstOrDefaultAsync(x => x.UserId == MeId && x.DeviceId == normalized, ct);
        if (key is null) return NotFound();
        if (key.IsRevoked) return BadRequest(new { message = "Revoked device cannot be verified." });

        key.IsTrusted = true;
        key.TrustState = TrustUserVerified;
        key.RequiresUserVerification = false;
        key.UserVerifiedAt = DateTime.UtcNow;
        key.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.E2eeDeviceUserVerified", "E2EEDevice", key.DeviceId, $"E2EE device user verified. fingerprint={key.Fingerprint}", ct);
        return Ok(ToDeviceDto(key));
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
        key.IsTrusted = false;
        key.TrustState = TrustRevoked;
        key.RequiresUserVerification = true;
        key.RevokedAt = DateTime.UtcNow;
        key.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.E2eeDeviceRevoked", "E2EEDevice", key.DeviceId, $"E2EE device revoked. fingerprint={key.Fingerprint}", ct);
        return NoContent();
    }

    [HttpGet("backup/status")]
    public async Task<ActionResult<E2eeBackupStatusDto>> GetBackupStatus(CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var backup = await db.E2eeEncryptedBackups.AsNoTracking()
            .Where(x => x.UserId == MeId)
            .Select(x => new E2eeBackupStatusDto(true, x.UpdatedAt, x.DeviceId, x.Version))
            .FirstOrDefaultAsync(ct);
        return Ok(backup ?? new E2eeBackupStatusDto(false, null, null, 0));
    }

    [HttpGet("backup")]
    public async Task<ActionResult<E2eeBackupDto>> GetBackup(CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var backup = await db.E2eeEncryptedBackups.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == MeId, ct);
        if (backup is null) return NotFound(new { message = "Encrypted backup not found." });
        return Ok(ToBackupDto(backup));
    }

    [HttpPut("backup")]
    public async Task<ActionResult<E2eeBackupStatusDto>> SaveBackup([FromBody] E2eeBackupSaveRequest request, CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        if (request is null) return BadRequest(new { message = "Backup payload is required." });

        var deviceId = CleanText(request.DeviceId, 64);
        if (string.IsNullOrWhiteSpace(deviceId)) return BadRequest(new { message = "DeviceId is required." });
        if (!LooksLikeBase64(request.SaltBase64, 256) || !LooksLikeBase64(request.NonceBase64, 256) || !LooksLikeBase64(request.TagBase64, 256) || !LooksLikeBase64(request.CiphertextBase64, 2_000_000))
            return BadRequest(new { message = "Invalid encrypted backup payload." });

        var now = DateTime.UtcNow;
        var backup = await db.E2eeEncryptedBackups.FirstOrDefaultAsync(x => x.UserId == MeId, ct);
        if (backup is null)
        {
            backup = new E2eeEncryptedBackup
            {
                Id = Guid.NewGuid(),
                UserId = MeId,
                CreatedAt = now
            };
            db.E2eeEncryptedBackups.Add(backup);
        }

        backup.DeviceId = deviceId;
        backup.PublicKeyFingerprint = CleanText(request.PublicKeyFingerprint, 128);
        backup.Kdf = CleanText(request.Kdf, 64) ?? "PBKDF2-SHA256-250000";
        backup.SaltBase64 = request.SaltBase64.Trim();
        backup.NonceBase64 = request.NonceBase64.Trim();
        backup.CiphertextBase64 = request.CiphertextBase64.Trim();
        backup.TagBase64 = request.TagBase64.Trim();
        backup.Version = Math.Clamp(request.Version, 1, 10);
        backup.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.E2eeBackupSaved", "E2EEBackup", backup.Id.ToString("D"), $"Encrypted E2EE backup saved. device={backup.DeviceId}; version={backup.Version}", ct);
        return Ok(new E2eeBackupStatusDto(true, backup.UpdatedAt, backup.DeviceId, backup.Version));
    }

    [HttpDelete("backup")]
    public async Task<IActionResult> DeleteBackup(CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var backup = await db.E2eeEncryptedBackups.FirstOrDefaultAsync(x => x.UserId == MeId, ct);
        if (backup is null) return NoContent();
        db.E2eeEncryptedBackups.Remove(backup);
        await db.SaveChangesAsync(ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.E2eeBackupDeleted", "E2EEBackup", backup.Id.ToString("D"), "Encrypted E2EE backup deleted.", ct);
        return NoContent();
    }

    private async Task<UserE2eeKey> UpsertDeviceInternalAsync(Guid userId, string deviceId, string publicKeyBase64, string? deviceName, bool replaceExisting, string? platform, string? clientVersion, CancellationToken ct)
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
        var fingerprint = Fingerprint(raw);
        var existing = await db.UserE2eeKeys.FirstOrDefaultAsync(x => x.UserId == userId && x.DeviceId == deviceId, ct);
        if (existing is not null && !replaceExisting)
        {
            existing.LastSeenAt = now;
            existing.LastIpAddress = CleanText(GetClientIp(), 64);
            existing.Platform = CleanText(platform, 64) ?? existing.Platform;
            existing.ClientVersion = CleanText(clientVersion, 32) ?? existing.ClientVersion;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var isNew = existing is null;
        var fingerprintChanged = existing is not null
            && !string.IsNullOrWhiteSpace(existing.Fingerprint)
            && !string.Equals(existing.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);

        if (existing is null)
        {
            existing = new UserE2eeKey
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DeviceId = deviceId,
                CreatedAt = now,
                IsTrusted = true,
                TrustState = TrustTofu,
                RequiresUserVerification = false
            };
            db.UserE2eeKeys.Add(existing);
        }

        var safeName = CleanText(deviceName, 128);
        existing.PublicKeyBase64 = Convert.ToBase64String(raw);
        existing.Algorithm = "ECDH-P256-SPKI";
        existing.Fingerprint = fingerprint;
        existing.DeviceName = safeName ?? existing.DeviceName;
        existing.DeviceKeyVersion = Math.Max(2, existing.DeviceKeyVersion);
        existing.IsRevoked = false;
        existing.RevokedAt = null;
        existing.LastSeenAt = now;
        existing.LastIpAddress = CleanText(GetClientIp(), 64) ?? existing.LastIpAddress;
        existing.Platform = CleanText(platform, 64) ?? existing.Platform;
        existing.ClientVersion = CleanText(clientVersion, 32) ?? existing.ClientVersion;

        if (isNew)
        {
            existing.IsTrusted = true;
            existing.TrustState = TrustTofu;
            existing.RequiresUserVerification = false;
        }
        else if (fingerprintChanged)
        {
            existing.IsTrusted = false;
            existing.TrustState = TrustUnknown;
            existing.RequiresUserVerification = true;
            existing.UserVerifiedAt = null;
        }

        existing.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        if (fingerprintChanged)
            await TryNotifySecurityKeyChangedAsync(userId, deviceId, existing.DeviceName, ct);
        log.LogInformation("E2EE device key registered. UserId={UserId} DeviceId={DeviceId} Fingerprint={Fingerprint} TrustState={TrustState}", userId, deviceId, existing.Fingerprint, existing.TrustState);
        return existing;
    }

    private async Task TryNotifySecurityKeyChangedAsync(Guid userId, string deviceId, string? deviceName, CancellationToken ct)
    {
        try
        {
            var userName = await db.Users.AsNoTracking()
                .Where(x => x.Id == userId)
                .Select(x => x.UserName)
                .FirstOrDefaultAsync(ct);
            userName = string.IsNullOrWhiteSpace(userName) ? "Пользователь" : userName.Trim();

            var peers = await db.Friendships.AsNoTracking()
                .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == userId || f.AddresseeId == userId))
                .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId)
                .Distinct()
                .ToListAsync(ct);

            if (peers.Count == 0) return;

            var safeDeviceName = string.IsNullOrWhiteSpace(deviceName) ? "устройстве" : deviceName.Trim();
            var text = $"{userName} обновил ключи безопасности на {safeDeviceName}. Если это выглядит неожиданно, уточните у него, всё ли в порядке.";

            foreach (var peerId in peers)
            {
                try
                {
                    var created = await directChat.CreateMessageAsync(userId, peerId, text, null, DirectMessageKind.System, "security-key-changed", null, ct);
                    var dto = await directChat.GetMessageDtoAsync(created.dialog.Id, created.message.Id, ct);
                    if (dto is null) continue;

                    await hub.Clients.User(userId.ToString()).SendAsync("ChatMessageCreated", new ChatRealtimeMessageDto(peerId, dto), ct);
                    await hub.Clients.User(peerId.ToString()).SendAsync("ChatMessageCreated", new ChatRealtimeMessageDto(userId, dto), ct);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Failed to notify peer about E2EE security key change. UserId={UserId} PeerId={PeerId}", userId, peerId);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to create E2EE security key change notifications. UserId={UserId} DeviceId={DeviceId}", userId, deviceId);
        }
    }

    private static E2eeBackupDto ToBackupDto(E2eeEncryptedBackup backup) => new(
        backup.DeviceId,
        backup.PublicKeyFingerprint,
        backup.Kdf,
        backup.SaltBase64,
        backup.NonceBase64,
        backup.CiphertextBase64,
        backup.TagBase64,
        backup.Version,
        backup.UpdatedAt);

    private static bool LooksLikeBase64(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) return false;
        try
        {
            _ = Convert.FromBase64String(value.Trim());
            return true;
        }
        catch
        {
            return false;
        }
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
        key.LastSeenAt,
        key.DeviceKeyVersion,
        key.TrustState,
        key.RequiresUserVerification,
        key.UserVerifiedAt,
        key.LastIpAddress,
        key.Platform,
        key.ClientVersion);

    private static string? NormalizeDeviceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        return s.Length > 64 ? s[..64] : s;
    }

    private string? GetClientIp()
    {
        var forwarded = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private static string? CleanText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        return s.Length > maxLength ? s[..maxLength] : s;
    }

    private static string Fingerprint(byte[] raw)
    {
        var hash = SHA256.HashData(raw);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
