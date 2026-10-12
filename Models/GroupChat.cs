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

    [MaxLength(512)]
    public string? AvatarUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<GroupChatMember> Members { get; set; } = new();
}
