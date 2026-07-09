using System;
using System.ComponentModel.DataAnnotations;
using JaeZoo.Server.Models;

namespace JaeZoo.Server.Models.Security;

public sealed class E2eeOneTimePreKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    [MaxLength(64)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string KeyId { get; set; } = string.Empty;

    [MaxLength(8192)]
    public string PublicKeyBase64 { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Algorithm { get; set; } = "ECDH-P256-SPKI";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClaimedAt { get; set; }
    public Guid? ClaimedByUserId { get; set; }

    [MaxLength(64)]
    public string? ClaimedByDeviceId { get; set; }

    public User? User { get; set; }
}
