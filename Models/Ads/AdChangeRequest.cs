using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models.Ads;

public enum AdChangeRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Withdrawn = 3,
}

/// <summary>
/// Заявка рекламного менеджера: предлагаемый список рекламы целиком. Владелец или администратор смотрит её в
/// Control Room (что добавится, уберётся, изменится — относительно того, что опубликовано сейчас) и одобряет
/// (список публикуется) или отклоняет с причиной. Реклама из Control Room публикуется сразу, без заявки.
/// </summary>
public sealed class AdChangeRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CreatedById { get; set; }

    [MaxLength(64)]
    public string CreatedByName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(500)]
    public string? Comment { get; set; }

    /// <summary>Предлагаемые элементы (JSON списка AdsManifestItem).</summary>
    public string ItemsJson { get; set; } = "[]";

    public AdChangeRequestStatus Status { get; set; } = AdChangeRequestStatus.Pending;
    public Guid? ReviewedById { get; set; }

    [MaxLength(64)]
    public string? ReviewedByName { get; set; }

    public DateTime? ReviewedAt { get; set; }

    [MaxLength(500)]
    public string? RejectReason { get; set; }
}
