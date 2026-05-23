using System.Security.Claims;
using System.Security.Cryptography;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/e2ee/keys")]
[Authorize]
public sealed class E2eeKeysController(AppDbContext db, ILogger<E2eeKeysController> log) : ControllerBase
{
    private Guid MeId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? User.FindFirstValue("nameid"), out var id)
        ? id
        : Guid.Empty;

    [HttpGet("{userId:guid}")]
    public async Task<ActionResult<E2eePublicKeyDto>> GetPublicKey(Guid userId, CancellationToken ct)
    {
        var key = await db.UserE2eeKeys.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (key is null || string.IsNullOrWhiteSpace(key.PublicKeyBase64))
            return NotFound(new { message = "E2EE public key not found." });

        return Ok(ToDto(key));
    }

    [HttpGet("me")]
    public async Task<ActionResult<E2eePublicKeyDto>> GetMyPublicKey(CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        var key = await db.UserE2eeKeys.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == MeId, ct);
        if (key is null || string.IsNullOrWhiteSpace(key.PublicKeyBase64))
            return NotFound(new { message = "E2EE public key not found." });

        return Ok(ToDto(key));
    }

    [HttpPost("me")]
    public async Task<ActionResult<E2eePublicKeyDto>> UpsertMyPublicKey([FromBody] UpsertE2eePublicKeyRequest request, CancellationToken ct)
    {
        if (MeId == Guid.Empty) return Unauthorized();
        if (request is null || string.IsNullOrWhiteSpace(request.PublicKeyBase64))
            return BadRequest(new { message = "Public key is required." });

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(request.PublicKeyBase64.Trim());
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportSubjectPublicKeyInfo(raw, out _);
        }
        catch
        {
            return BadRequest(new { message = "Invalid E2EE public key." });
        }

        if (raw.Length > 4096)
            return BadRequest(new { message = "Public key is too large." });

        var fingerprint = Fingerprint(raw);
        var now = DateTime.UtcNow;
        var existing = await db.UserE2eeKeys.FirstOrDefaultAsync(x => x.UserId == MeId, ct);

        if (existing is not null && !request.ReplaceExisting)
        {
            // Do not silently rotate another device's account key. Multi-device key sync will be a separate stage.
            return Ok(ToDto(existing));
        }

        if (existing is null)
        {
            existing = new UserE2eeKey
            {
                UserId = MeId,
                CreatedAt = now
            };
            db.UserE2eeKeys.Add(existing);
        }

        existing.PublicKeyBase64 = Convert.ToBase64String(raw);
        existing.Algorithm = "ECDH-P256-SPKI";
        existing.Fingerprint = fingerprint;
        var deviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? null : request.DeviceName.Trim();
        existing.DeviceName = deviceName is null ? null : deviceName[..Math.Min(128, deviceName.Length)];
        existing.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        log.LogInformation("E2EE public key registered. UserId={UserId} Fingerprint={Fingerprint}", MeId, fingerprint);
        return Ok(ToDto(existing));
    }

    private static E2eePublicKeyDto ToDto(UserE2eeKey key) => new(
        key.UserId,
        key.PublicKeyBase64,
        key.Algorithm,
        key.Fingerprint,
        key.UpdatedAt);

    private static string Fingerprint(byte[] raw)
    {
        var hash = SHA256.HashData(raw);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
