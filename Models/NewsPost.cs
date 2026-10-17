using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

/// <summary>Новость во вкладке «Новости» клиента. Публичный контент JaeZoo, не переписка — хранится открытым текстом.</summary>
public class NewsPost
{
    public const int CategoryNews = 0;
    public const int CategoryUpdate = 1;
    public const int CategoryEvent = 2;
    public const int CategoryMaintenance = 3;
    public const int CategoryImportant = 4;

    public static bool IsKnownCategory(int category) => category is >= CategoryNews and <= CategoryImportant;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }

    public int Category { get; set; }

    [MaxLength(160)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Короткий анонс для карточки; пусто — клиент берёт начало текста.</summary>
    [MaxLength(400)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>Текст с простой разметкой: «# »/«## » заголовки, «- » списки, **жирный**, [ссылка](https://…).</summary>
    [MaxLength(20000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>Ключ обложки в хранилище (news/covers/…); null — без обложки.</summary>
    [MaxLength(256)]
    public string? CoverKey { get; set; }

    /// <summary>Версия клиента для новостей-обновлений.</summary>
    [MaxLength(32)]
    public string? Version { get; set; }

    public bool IsPinned { get; set; }

    /// <summary>Отправлена ли новость в личку от JaeZoo.</summary>
    public bool SentToSystemChat { get; set; }

    public Guid? CreatedByUserId { get; set; }

    [MaxLength(64)]
    public string CreatedByName { get; set; } = string.Empty;
}
