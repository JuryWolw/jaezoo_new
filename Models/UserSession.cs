using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public sealed class UserSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    [MaxLength(128)]
    public string RefreshTokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastRefreshAt { get; set; }

    [MaxLength(64)]
    public string IpAddress { get; set; } = string.Empty;

    [MaxLength(256)]
    public string UserAgent { get; set; } = string.Empty;

    [MaxLength(128)]
    public string DeviceName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Platform { get; set; } = string.Empty;

    [MaxLength(32)]
    public string ClientVersion { get; set; } = string.Empty;

    public bool IsTrusted { get; set; }

    [MaxLength(128)]
    public string? FingerprintHash { get; set; }

    public User? User { get; set; }
}
