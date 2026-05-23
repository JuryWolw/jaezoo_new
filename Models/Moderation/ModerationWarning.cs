using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models.Moderation;

public sealed class ModerationWarning
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? ReportId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(160)]
    public string EmailSubject { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string EmailBody { get; set; } = string.Empty;
}
