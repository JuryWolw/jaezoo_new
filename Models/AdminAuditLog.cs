using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public sealed class AdminAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ActorUserId { get; set; }

    [MaxLength(64)]
    public string ActorPublicId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string ActorDisplayName { get; set; } = string.Empty;

    [MaxLength(80)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(64)]
    public string TargetType { get; set; } = string.Empty;

    [MaxLength(128)]
    public string TargetId { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Summary { get; set; } = string.Empty;

    [MaxLength(64)]
    public string IpAddress { get; set; } = string.Empty;

    [MaxLength(256)]
    public string UserAgent { get; set; } = string.Empty;
}
