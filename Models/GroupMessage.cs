using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class GroupMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required] public Guid GroupChatId { get; set; }
    [Required] public Guid SenderId { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    public DirectMessageKind Kind { get; set; } = DirectMessageKind.User;

    /// <summary>
    /// Group security epoch at the moment this message was accepted.
    /// Used by clients to validate E2EE membership context.
    /// </summary>
    public int GroupSecurityEpoch { get; set; } = 1;

    [MaxLength(64)]
    public string? SystemKey { get; set; }

    public Guid? ForwardedFromMessageId { get; set; }

    public DateTime? EditedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedById { get; set; }

    public List<GroupMessageAttachment> Attachments { get; set; } = new();
}
