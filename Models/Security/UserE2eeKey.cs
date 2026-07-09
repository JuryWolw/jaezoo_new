using System;
using System.ComponentModel.DataAnnotations;
using JaeZoo.Server.Models;

namespace JaeZoo.Server.Models.Security;

public sealed class UserE2eeKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    [MaxLength(64)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(8192)]
    public string PublicKeyBase64 { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Algorithm { get; set; } = "ECDH-P256-SPKI";

    [MaxLength(128)]
    public string Fingerprint { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? DeviceName { get; set; }

    public bool IsRevoked { get; set; }
    public bool IsTrusted { get; set; } = true;

    /// <summary>
    /// Preparation marker for the next E2EE patches.
    /// 2 = current static ECDH multi-device key.
    /// Future values will describe X3DH/Double Ratchet capable devices.
    /// </summary>
    public int DeviceKeyVersion { get; set; } = 2;

    /// <summary>
    /// Future trust state. 0 = unknown, 1 = TOFU trusted, 2 = user verified, 3 = revoked/blocked.
    /// This patch does not enforce it yet.
    /// </summary>
    public int TrustState { get; set; } = 1;

    public bool RequiresUserVerification { get; set; } = false;
    public DateTime? UserVerifiedAt { get; set; }

    [MaxLength(64)]
    public string? LastIpAddress { get; set; }

    [MaxLength(64)]
    public string? Platform { get; set; }

    [MaxLength(32)]
    public string? ClientVersion { get; set; }

    public DateTime? RevokedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}
