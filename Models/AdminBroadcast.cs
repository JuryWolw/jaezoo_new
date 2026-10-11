using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

/// <summary>Рассылка из админки: в строку уведомлений и/или в системный чат JaeZoo.</summary>
public class AdminBroadcast
{
    public const int ChannelNotifications = 1;
    public const int ChannelSystemChat = 2;

    public const int AudienceAll = 0;
    public const int AudienceStaff = 1;
    public const int AudienceUser = 2;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid CreatedByUserId { get; set; }

    [MaxLength(64)]
    public string CreatedByName { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string Text { get; set; } = string.Empty;

    /// <summary>Флаги каналов: 1 — уведомления, 2 — системный чат.</summary>
    public int Channels { get; set; }

    /// <summary>0 — все пользователи, 1 — только персонал (пользователи с ролями), 2 — конкретный пользователь.</summary>
    public int Audience { get; set; }

    /// <summary>Получатель личного сообщения от JaeZoo (Audience = 2).</summary>
    public Guid? TargetUserId { get; set; }

    [MaxLength(64)]
    public string? TargetName { get; set; }

    /// <summary>Уведомление: true — показать при следующем входе тем, кто сейчас не в сети; false — только онлайн.</summary>
    public bool DeliverOffline { get; set; }

    /// <summary>До какого момента уведомление доставляется при входе.</summary>
    public DateTime ExpiresAt { get; set; }

    public int RecipientsCount { get; set; }

    /// <summary>Картинки для системного чата (Id файлов через запятую).</summary>
    [MaxLength(1024)]
    public string? FileIdsCsv { get; set; }
}
