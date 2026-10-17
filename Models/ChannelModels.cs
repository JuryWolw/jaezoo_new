using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

/// <summary>Бан в канале/группе: пользователь удалён и не может вернуться (вступить сам или по ссылке).</summary>
public class GroupBan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupChatId { get; set; }
    public Guid UserId { get; set; }
    public Guid BannedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(200)]
    public string? Reason { get; set; }
}

/// <summary>Пригласительная ссылка в канал/группу: код, срок, лимит вступлений, отзыв.</summary>
public class GroupInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupChatId { get; set; }

    /// <summary>Случайный код ссылки jaezoo.ru/join/{code}.</summary>
    [MaxLength(32)]
    public string Code { get; set; } = string.Empty;

    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public int? MaxUses { get; set; }
    public int Uses { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>Просмотр поста канала: один на пользователя (счётчик просмотров).</summary>
public class GroupMessageView
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public DateTime ViewedAt { get; set; } = DateTime.UtcNow;
}
