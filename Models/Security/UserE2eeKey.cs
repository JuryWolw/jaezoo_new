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
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}
