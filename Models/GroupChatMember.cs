namespace JaeZoo.Server.Models;

public class GroupChatMember
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GroupChatId { get; set; }

    public Guid UserId { get; set; }

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastReadAt { get; set; } = DateTime.MinValue;

    public Guid LastReadMessageId { get; set; } = Guid.Empty;
}
