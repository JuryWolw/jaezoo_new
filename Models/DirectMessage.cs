using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class DirectMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required] public Guid DialogId { get; set; }
    [Required] public Guid SenderId { get; set; }

    // Важно: теперь текст может быть пустым (когда сообщение содержит только вложения).
    [MaxLength(4000)]
    public string Text { get; set; } = string.Empty;

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    // NEW: attachments navigation (не ломает БД, но помогает удобно работать)
    public List<DirectMessageAttachment> Attachments { get; set; } = new();
}
