using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

/// <summary>Страница вкладки клиента («Поддержка», «Сотрудничество», FAQ), которую правят из админки. Json — проверенный документ.</summary>
public class SiteContent
{
    [Key, MaxLength(32)]
    public string Key { get; set; } = string.Empty;

    public string Json { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string? UpdatedByName { get; set; }
}

/// <summary>Контакт на странице: вид (telegram, vk, max, mail, link — задаёт значок и цвет), подпись, текст, ссылка.</summary>
public sealed record ContactItemDto(string Kind, string Title, string Text, string? Url = null, string? CopyText = null, bool Soon = false);

public sealed record ContactSectionDto(string Title, string? Description, List<ContactItemDto> Items);

/// <summary>Страница контактов («Поддержка», «Сотрудничество»).</summary>
public sealed record ContactPageDto(List<ContactSectionDto> Sections);

public sealed record FaqEntryDto(string Question, string Answer);

public sealed record FaqPageDto(List<FaqEntryDto> Items);

/// <summary>Страница для клиента и админки: ключ, документ, когда и кем изменена (null — встроенная по умолчанию).</summary>
public sealed record SiteContentDto(string Key, string Json, DateTime? UpdatedAt, string? UpdatedByName);

public sealed record SaveSiteContentRequest(string Json);

/// <summary>SignalR ContentChanged: страница изменилась — клиенты перечитывают её.</summary>
public sealed record SiteContentChangedDto(string Key);
