using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class GroupVoiceParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    public Guid GroupChatId { get; set; }

    public Guid UserId { get; set; }

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public DateTime? LeftAt { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(256)]
    public string? ClientInfo { get; set; }
}
