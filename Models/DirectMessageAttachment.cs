using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class DirectMessageAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid MessageId { get; set; }

    [Required]
    public Guid FileId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
