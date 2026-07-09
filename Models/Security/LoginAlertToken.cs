using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models.Security;

public sealed class LoginAlertToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? SessionId { get; set; }

    [MaxLength(128)]
    public string TokenHash { get; set; } = string.Empty;

    [MaxLength(64)]
    public string IpAddress { get; set; } = string.Empty;

    [MaxLength(128)]
    public string DeviceName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Platform { get; set; } = string.Empty;

    [MaxLength(32)]
    public string ClientVersion { get; set; } = string.Empty;

    public bool IsKnownDevice { get; set; }
    public bool UsedTwoFactor { get; set; }
    public bool UsedRecoveryCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }

    [MaxLength(64)]
    public string? UsedIpAddress { get; set; }

    public JaeZoo.Server.Models.User? User { get; set; }
}
