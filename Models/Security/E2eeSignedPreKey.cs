using System;
using System.ComponentModel.DataAnnotations;
using JaeZoo.Server.Models;

namespace JaeZoo.Server.Models.Security;

public sealed class E2eeSignedPreKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    [MaxLength(64)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string KeyId { get; set; } = string.Empty;

    [MaxLength(8192)]
    public string PublicKeyBase64 { get; set; } = string.Empty;

    [MaxLength(8192)]
    public string SignatureBase64 { get; set; } = string.Empty;

    [MaxLength(96)]
    public string Algorithm { get; set; } = "ECDH-P256-SPKI+ECDSA-P256-SHA256";

    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}
