using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class DirectMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required] public Guid DialogId { get; set; }
    [Required] public Guid SenderId { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Server-side marker of the client E2EE envelope version.
    /// 0 means plaintext or legacy server-side protected text, 1 means direct v1,
    /// 2 means current multi-device direct envelope.
    /// The server never decrypts the payload. This marker is only for safe migration.
    /// </summary>
    public int E2eeEnvelopeVersion { get; set; } = 0;

    [MaxLength(64)]
    public string? E2eeProtocol { get; set; }

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
