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

    /// <summary>Public groups are searchable and can be joined by verified users. Private groups are invite-only.</summary>
    public bool IsPublic { get; set; } = false;

    /// <summary>
    /// Monotonically increasing security epoch for group E2EE.
    /// It changes whenever group membership changes, so new messages are bound
    /// to the exact membership version used by the sender.
    /// </summary>
    public int SecurityEpoch { get; set; } = 1;

    public DateTime SecurityEpochChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Future E2EE history policy.
    /// 0 = new members read only future messages.
    /// 1 = owner/admin can share encrypted old history keys.
    /// 2 = history sharing disabled for this group.
    /// Current patch only stores the policy and does not change access logic yet.
    /// </summary>
    public int HistoryPolicy { get; set; } = 0;

    public DateTime? HistoryPolicyChangedAt { get; set; }

    [MaxLength(512)]
    public string? AvatarUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<GroupChatMember> Members { get; set; } = new();
}
