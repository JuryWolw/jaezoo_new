using System;
using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models.Security;

public sealed class TwoFactorLoginChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    [MaxLength(128)]
    public string ChallengeTokenHash { get; set; } = string.Empty;

    public bool RememberMe { get; set; }

    [MaxLength(128)]
    public string DeviceName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Platform { get; set; } = string.Empty;

    [MaxLength(32)]
    public string ClientVersion { get; set; } = string.Empty;

    [MaxLength(64)]
    public string IpAddress { get; set; } = string.Empty;

    [MaxLength(256)]
    public string UserAgent { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(5);
    public DateTime? UsedAt { get; set; }

    public User? User { get; set; }
}
