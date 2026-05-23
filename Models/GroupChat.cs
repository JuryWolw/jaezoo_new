using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class GroupChat
{
    public const int DefaultMemberLimit = 50;

    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(120)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    public Guid OwnerId { get; set; }

    public int MemberLimit { get; set; } = DefaultMemberLimit;

    /// <summary>
    /// Monotonically increasing security epoch for group E2EE.
    /// It changes whenever group membership changes, so new messages are bound
    /// to the exact membership version used by the sender.
    /// </summary>
    public int SecurityEpoch { get; set; } = 1;

    public DateTime SecurityEpochChangedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(512)]
    public string? AvatarUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<GroupChatMember> Members { get; set; } = new();
}
