using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public enum GroupVoiceSessionState
{
    Active = 0,
    Ended = 1
}

public class GroupVoiceSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GroupChatId { get; set; }

    [MaxLength(160)]
    public string RoomName { get; set; } = string.Empty;

    public Guid StartedByUserId { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    public DateTime? EndedAt { get; set; }

    public GroupVoiceSessionState State { get; set; } = GroupVoiceSessionState.Active;

    public List<GroupVoiceParticipant> Participants { get; set; } = new();
}
