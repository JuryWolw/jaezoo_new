using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

/// <summary>Плашка у ника («Разработчик», «Тестер», «Партнёр» …): создаётся и выдаётся из админки.</summary>
public class Badge
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(40)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Цвет плашки, #RRGGBB.</summary>
    [MaxLength(9)]
    public string ColorHex { get; set; } = "#E30613";

    /// <summary>Значок слева от подписи: символ Segoe MDL2 или эмодзи; null — без значка.</summary>
    [MaxLength(8)]
    public string? Icon { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }

    /// <summary>Порядок у ника: меньше — левее.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Выданная пользователю плашка.</summary>
public class UserBadge
{
    public Guid UserId { get; set; }
    public Guid BadgeId { get; set; }
    public Guid? GrantedById { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
}
