using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class DirectMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required] public Guid DialogId { get; set; }
    [Required] public Guid SenderId { get; set; }

    [MaxLength(4000)]
    public string Text { get; set; } = string.Empty;

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    public DirectMessageKind Kind { get; set; } = DirectMessageKind.User;

    [MaxLength(64)]
    public string? SystemKey { get; set; }

    public Guid? ForwardedFromMessageId { get; set; }

    public DateTime? EditedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedById { get; set; }

    public List<DirectMessageAttachment> Attachments { get; set; } = new();
}
