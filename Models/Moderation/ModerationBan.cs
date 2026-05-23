using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models.Moderation;

public sealed class ModerationBan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? RevokedByUserId { get; set; }

    [MaxLength(64)]
    public string Type { get; set; } = "Account";

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? RevokeReason { get; set; }

    public bool IsActive(DateTime utcNow) => RevokedAt == null && (ExpiresAt == null || ExpiresAt > utcNow);
}
