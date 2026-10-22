using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class GroupMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required] public Guid GroupChatId { get; set; }
    [Required] public Guid SenderId { get; set; }

    /// <summary>Plain text only for <see cref="DirectMessageKind.System"/> (service notices). Empty for user messages.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>User messages: opaque MLS PrivateMessage. Reply/forward/attachment keys live inside it.</summary>
    public byte[]? Ciphertext { get; set; }

    /// <summary>MLS epoch the ciphertext was encrypted in (delivery metadata).</summary>
    public long MlsEpoch { get; set; }

    public Guid? SenderDeviceId { get; set; }

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    public DirectMessageKind Kind { get; set; } = DirectMessageKind.User;

    [MaxLength(64)]
    public string? SystemKey { get; set; }

    /// <summary>
    /// Канал: null — пост ленты; id поста — комментарий под ним; id самого канала — сообщение беседы канала.
    /// Сервер видит, к чему относится комментарий (иначе не отличить его от попытки опубликовать пост), но не текст.
    /// </summary>
    public Guid? ThreadRootId { get; set; }

    /// <summary>Пост канала: комментарии под ним выключены.</summary>
    public bool CommentsClosed { get; set; }

    /// <summary>Ответ — только у открытых комментариев открытого канала (в MLS ответ лежит внутри шифротекста).</summary>
    public Guid? ReplyToId { get; set; }

    public DateTime? EditedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedById { get; set; }

    public List<GroupMessageAttachment> Attachments { get; set; } = new();
}
