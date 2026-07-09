using System;
using System.ComponentModel.DataAnnotations;
using JaeZoo.Server.Models;

namespace JaeZoo.Server.Models.Security;

public sealed class E2eeEncryptedBackup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    [MaxLength(64)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? PublicKeyFingerprint { get; set; }

    [MaxLength(64)]
    public string Kdf { get; set; } = "PBKDF2-SHA256-250000";

    [MaxLength(256)]
    public string SaltBase64 { get; set; } = string.Empty;

    [MaxLength(256)]
    public string NonceBase64 { get; set; } = string.Empty;

    public string CiphertextBase64 { get; set; } = string.Empty;

    [MaxLength(256)]
    public string TagBase64 { get; set; } = string.Empty;

    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}
