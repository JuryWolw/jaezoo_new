using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public enum EmailVerificationPurpose
{
    EmailConfirmation = 0
}

public class EmailVerificationCode
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public EmailVerificationPurpose Purpose { get; set; } = EmailVerificationPurpose.EmailConfirmation;

    [MaxLength(128)]
    public string CodeHash { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Salt { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTime LastSentAt { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    [MaxLength(256)]
    public string? UserAgent { get; set; }
}
