namespace JaeZoo.Server.Models;

/// <summary>Шапка группы или канала (как шапка профиля): картинка хранится в базе, последняя — текущая.</summary>
public class GroupBanner
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GroupChatId { get; set; }

    public byte[] Data { get; set; } = System.Array.Empty<byte>();

    public string ContentType { get; set; } = "image/png";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
