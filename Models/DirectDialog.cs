namespace JaeZoo.Server.Models;

public class DirectDialog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid User1Id { get; set; } // всегда «меньший» по Guid
    public Guid User2Id { get; set; } // всегда «больший» по Guid
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ===== Read-state (железно, как в Discord/Telegram) =====
    // Композитный курсор (SentAt, MessageId) для каждого участника.
    // Значения по умолчанию: MinValue/Empty => "ничего не прочитано".
    public DateTime LastReadAtUser1 { get; set; } = DateTime.MinValue;
    public Guid LastReadMessageIdUser1 { get; set; } = Guid.Empty;

    public DateTime LastReadAtUser2 { get; set; } = DateTime.MinValue;
    public Guid LastReadMessageIdUser2 { get; set; } = Guid.Empty;
}
