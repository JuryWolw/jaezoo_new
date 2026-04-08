namespace JaeZoo.Server.Models;

public class GroupMessageAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MessageId { get; set; }

    public Guid FileId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
