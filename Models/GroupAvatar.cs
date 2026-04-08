namespace JaeZoo.Server.Models;

public class GroupAvatar
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GroupChatId { get; set; }

    public byte[] Data { get; set; } = System.Array.Empty<byte>();

    public string ContentType { get; set; } = "image/png";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
