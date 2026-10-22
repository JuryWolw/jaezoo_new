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

    /// <summary>Шапка (как у профиля): ссылка с версией на /banners/groups/{id}, null — без шапки.</summary>
    [MaxLength(512)]
    public string? BannerUrl { get; set; }

    /// <summary>Группа или канал. Задаётся при создании и не меняется.</summary>
    public GroupChatKind Kind { get; set; } = GroupChatKind.Group;

    /// <summary>Канал: подпись автора под постом (иначе пост — от имени канала).</summary>
    public bool SignPosts { get; set; }

    /// <summary>Канал: какие комментарии включены (ветки под постами, беседа).</summary>
    public ChannelCommentsMode CommentsMode { get; set; } = ChannelCommentsMode.Threads;

    /// <summary>Канал: закреплённый пост.</summary>
    public Guid? PinnedMessageId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<GroupChatMember> Members { get; set; } = new();
}
