using System;
using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models.Security;

public sealed class TwoFactorRecoveryCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    [MaxLength(128)]
    public string CodeHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UsedAt { get; set; }

    [MaxLength(64)]
    public string? UsedIpAddress { get; set; }

    public User? User { get; set; }
}
