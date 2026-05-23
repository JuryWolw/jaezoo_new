using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models.Moderation;

public sealed class ModerationReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid ReporterUserId { get; set; }
    public Guid? TargetUserId { get; set; }
    public Guid? TargetMessageId { get; set; }
    public Guid? TargetGroupId { get; set; }

    [MaxLength(32)]
    public string TargetType { get; set; } = "User";

    [MaxLength(128)]
    public string TargetId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Details { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Status { get; set; } = "Open";

    public Guid? ModeratorUserId { get; set; }
    public DateTime? ResolvedAt { get; set; }

    [MaxLength(2000)]
    public string? ModerationNote { get; set; }
}
