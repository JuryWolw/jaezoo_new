using System.Security.Claims;
using System.Text.Json;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Ads;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services.Admin;
using JaeZoo.Server.Services.Ads;
using JaeZoo.Server.Services.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

public sealed record SubmitAdRequest(List<AdsManifestItem>? Items, string? Comment);
public sealed record RejectAdRequest(string? Reason);
public sealed record AdRequestItemDto(string Change, AdminAdBannerItem Item, AdminAdBannerItem? Before);
public sealed record AdRequestDto(Guid Id, DateTime CreatedAt, Guid CreatedById, string CreatedByName, string? Comment, string Status,
    string? ReviewedByName, DateTime? ReviewedAt, string? RejectReason, List<AdRequestItemDto> Changes);

/// <summary>
/// Реклама. Владелец и администраторы публикуют список сразу. Рекламный менеджер публикует только через заявку:
/// она приходит владельцу и администраторам (системный чат + раздел «Реклама» в Control Room), те одобряют
/// (список публикуется) или отклоняют с причиной. Правила проверяются здесь, а не только в окнах.
/// </summary>
[ApiController]
[Authorize(Policy = AuthPolicies.ManageAds)]
[Route("api/admin/ads")]
public sealed class AdminAdsController(IAdsService ads, AdminAuditService audit, AppDbContext db, SystemChatService systemChat, ILogger<AdminAdsController> log) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private bool CanPublish => AuthPolicies.AdminRoles.Any(User.IsInRole);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => Ok(await ads.GetAdminManifestAsync(cancellationToken));

    [HttpPut("manifest")]
    [Authorize(Policy = AuthPolicies.PublishAds)]
    public async Task<IActionResult> Save([FromBody] SaveAdsManifestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var before = await ads.GetAdminManifestAsync(cancellationToken);
            var manifest = await ads.SaveManifestAsync(request, cancellationToken);
            await audit.WriteAsync(User, HttpContext, "AdManifestPublished", "Ads", "manifest",
                $"Реклама опубликована: {DescribeChanges(before.Items, manifest.Items)}", cancellationToken);
            return Ok(manifest);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("images")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage(IFormFile file, CancellationToken cancellationToken)
    {
        try
        {
            // Загрузка картинки ничего не публикует: в рекламу она попадёт только с одобренной заявкой.
            var response = await ads.UploadImageAsync(file, cancellationToken);
            await audit.WriteAsync(User, HttpContext, "AdImageUploaded", "AdsImage", response.ImageKey, $"Загружена картинка рекламы {file.FileName}", cancellationToken);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("images")]
    public async Task<IActionResult> DeleteImage([FromQuery] string imageKey, CancellationToken cancellationToken)
    {
        if (!CanPublish)
        {
            // Менеджер не может удалить картинку, которая сейчас в показе или в чужой заявке на проверке.
            var live = await ads.GetAdminManifestAsync(cancellationToken);
            if (live.Items.Any(i => string.Equals(i.ImageKey, imageKey, StringComparison.OrdinalIgnoreCase)))
                return StatusCode(StatusCodes.Status403Forbidden, "Картинка сейчас в показе — сначала уберите рекламу через заявку.");
            var pending = await db.AdChangeRequests.AsNoTracking().Where(r => r.Status == AdChangeRequestStatus.Pending).Select(r => r.ItemsJson).ToListAsync(cancellationToken);
            if (pending.Any(j => j.Contains(imageKey, StringComparison.OrdinalIgnoreCase)))
                return StatusCode(StatusCodes.Status403Forbidden, "Картинка используется в заявке на проверке.");
        }
        await ads.DeleteImageAsync(imageKey, cancellationToken);
        await audit.WriteAsync(User, HttpContext, "AdImageDeleted", "AdsImage", imageKey, "Удалена картинка рекламы", cancellationToken);
        return NoContent();
    }

    // ===================== заявки =====================

    [HttpPost("requests")]
    public async Task<ActionResult<AdRequestDto>> Submit([FromBody] SubmitAdRequest request, CancellationToken ct)
    {
        var items = request.Items ?? new List<AdsManifestItem>();
        if (items.Count > 100) return BadRequest("Слишком много элементов рекламы.");
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is { Length: > 500 }) comment = comment[..500];

        var entity = new AdChangeRequest
        {
            CreatedById = ActorId(),
            CreatedByName = ActorName(),
            Comment = comment,
            ItemsJson = JsonSerializer.Serialize(items, Json),
        };
        db.AdChangeRequests.Add(entity);
        await db.SaveChangesAsync(ct);

        var live = await ads.GetAdminManifestAsync(ct);
        var summary = DescribeChanges(live.Items, items.Select(ToBanner).ToList());
        await audit.WriteAsync(User, HttpContext, "AdRequestSubmitted", "AdRequest", entity.Id.ToString(), $"Заявка на рекламу: {summary}", ct);
        await NotifyReviewersAsync($"📣 Новая заявка на рекламу от {entity.CreatedByName}: {summary}.\nОткройте JaeZoo Control Room → Реклама → Заявки.", ct);
        return await ToDtoAsync(entity, live, ct);
    }

    [HttpGet("requests")]
    public async Task<ActionResult<List<AdRequestDto>>> Requests([FromQuery] string? status, CancellationToken ct)
    {
        var query = db.AdChangeRequests.AsNoTracking().AsQueryable();
        if (!CanPublish)
        {
            var me = ActorId();
            query = query.Where(r => r.CreatedById == me); // менеджер видит только свои заявки
        }
        if (Enum.TryParse<AdChangeRequestStatus>(status, true, out var s))
            query = query.Where(r => r.Status == s);
        var rows = await query.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        var live = await ads.GetAdminManifestAsync(ct);
        var result = new List<AdRequestDto>();
        foreach (var r in rows)
            result.Add(await ToDtoAsync(r, live, ct));
        return result;
    }

    [HttpPost("requests/{id:guid}/approve")]
    [Authorize(Policy = AuthPolicies.PublishAds)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var entity = await db.AdChangeRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return NotFound();
        if (entity.Status != AdChangeRequestStatus.Pending) return Conflict("Заявка уже рассмотрена.");

        var items = JsonSerializer.Deserialize<List<AdsManifestItem>>(entity.ItemsJson, Json) ?? new();
        var before = await ads.GetAdminManifestAsync(ct);
        try
        {
            await ads.SaveManifestAsync(new SaveAdsManifestRequest { Version = before.Version, Items = items }, ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        entity.Status = AdChangeRequestStatus.Approved;
        entity.ReviewedById = ActorId();
        entity.ReviewedByName = ActorName();
        entity.ReviewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "AdRequestApproved", "AdRequest", id.ToString(),
            $"Одобрена заявка {entity.CreatedByName}: {DescribeChanges(before.Items, items.Select(ToBanner).ToList())}", ct);
        await TryNotifyAsync(entity.CreatedById, $"✅ Ваша заявка на рекламу одобрена ({entity.ReviewedByName}) и опубликована.", ct);
        return NoContent();
    }

    [HttpPost("requests/{id:guid}/reject")]
    [Authorize(Policy = AuthPolicies.PublishAds)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectAdRequest body, CancellationToken ct)
    {
        var entity = await db.AdChangeRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return NotFound();
        if (entity.Status != AdChangeRequestStatus.Pending) return Conflict("Заявка уже рассмотрена.");

        var reason = string.IsNullOrWhiteSpace(body?.Reason) ? null : body!.Reason!.Trim();
        if (reason is { Length: > 500 }) reason = reason[..500];
        entity.Status = AdChangeRequestStatus.Rejected;
        entity.RejectReason = reason;
        entity.ReviewedById = ActorId();
        entity.ReviewedByName = ActorName();
        entity.ReviewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "AdRequestRejected", "AdRequest", id.ToString(),
            $"Отклонена заявка {entity.CreatedByName}" + (reason == null ? string.Empty : $": {reason}"), ct);
        await TryNotifyAsync(entity.CreatedById, $"❌ Ваша заявка на рекламу отклонена ({entity.ReviewedByName})." + (reason == null ? string.Empty : $"\nПричина: {reason}"), ct);
        return NoContent();
    }

    [HttpPost("requests/{id:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct)
    {
        var entity = await db.AdChangeRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return NotFound();
        if (entity.CreatedById != ActorId()) return Forbid();
        if (entity.Status != AdChangeRequestStatus.Pending) return Conflict("Заявка уже рассмотрена.");
        entity.Status = AdChangeRequestStatus.Withdrawn;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(User, HttpContext, "AdRequestWithdrawn", "AdRequest", id.ToString(), "Заявка на рекламу отозвана автором", ct);
        return NoContent();
    }

    // ===================== помощники =====================

    private Task<AdRequestDto> ToDtoAsync(AdChangeRequest r, AdminAdsManifestResponse live, CancellationToken ct)
    {
        var proposed = (JsonSerializer.Deserialize<List<AdsManifestItem>>(r.ItemsJson, Json) ?? new()).Select(ToBanner).ToList();
        // Сравнение — с тем, что опубликовано сейчас (а не на момент подачи): одобрение заменит именно его.
        var changes = new List<AdRequestItemDto>();
        foreach (var item in proposed)
        {
            var old = live.Items.FirstOrDefault(x => x.Id == item.Id);
            var change = old == null ? "added" : SameAd(old, item) ? "same" : "changed";
            changes.Add(new AdRequestItemDto(change, item, old));
        }
        foreach (var gone in live.Items.Where(x => proposed.All(p => p.Id != x.Id)))
            changes.Add(new AdRequestItemDto("removed", gone, null));
        return Task.FromResult(new AdRequestDto(r.Id, r.CreatedAt, r.CreatedById, r.CreatedByName, r.Comment, r.Status.ToString(),
            r.ReviewedByName, r.ReviewedAt, r.RejectReason, changes));
    }

    private AdminAdBannerItem ToBanner(AdsManifestItem x) => new()
    {
        Id = x.Id,
        Title = x.Title,
        ImageKey = x.ImageKey,
        ImageUrl = ads.GetImageUrl(x.ImageKey),
        TargetUrl = x.TargetUrl,
        Enabled = x.Enabled,
        Priority = x.Priority,
        StartAtUtc = x.StartAtUtc,
        EndAtUtc = x.EndAtUtc,
    };

    private static bool SameAd(AdminAdBannerItem a, AdminAdBannerItem b) =>
        a.Title == b.Title && a.ImageKey == b.ImageKey && a.TargetUrl == b.TargetUrl && a.Enabled == b.Enabled
        && a.Priority == b.Priority && a.StartAtUtc == b.StartAtUtc && a.EndAtUtc == b.EndAtUtc;

    /// <summary>Короткое описание для аудита и уведомлений: «+1 «Скидки», −1, изменено 2».</summary>
    private static string DescribeChanges(IReadOnlyList<AdminAdBannerItem> before, IReadOnlyList<AdminAdBannerItem> after)
    {
        var added = after.Where(a => before.All(b => b.Id != a.Id)).Select(a => $"«{a.Title}»").ToList();
        var removed = before.Where(b => after.All(a => a.Id != b.Id)).Select(b => $"«{b.Title}»").ToList();
        var changed = after.Count(a => before.FirstOrDefault(b => b.Id == a.Id) is { } old && !SameAd(old, a));
        var parts = new List<string>();
        if (added.Count > 0) parts.Add("добавлено " + string.Join(", ", added));
        if (removed.Count > 0) parts.Add("убрано " + string.Join(", ", removed));
        if (changed > 0) parts.Add($"изменено {changed}");
        return parts.Count == 0 ? "без изменений" : string.Join("; ", parts);
    }

    private async Task NotifyReviewersAsync(string text, CancellationToken ct)
    {
        var reviewers = await db.UserRoles.AsNoTracking()
            .Where(r => r.RevokedAt == null && (r.Role == GlobalRole.Owner || r.Role == GlobalRole.Admin))
            .Select(r => r.UserId).Distinct().ToListAsync(ct);
        foreach (var id in reviewers)
            await TryNotifyAsync(id, text, ct);
    }

    private async Task TryNotifyAsync(Guid userId, string text, CancellationToken ct)
    {
        try { await systemChat.PostAsync(userId, "ads-review", text, ct: ct); }
        catch (Exception ex) { log.LogWarning(ex, "Ads review notification failed for {UserId}.", userId); }
    }

    private Guid ActorId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }

    private string ActorName()
    {
        var name = User.FindFirstValue("display_name") ?? "Сотрудник";
        return name.Length <= 64 ? name : name[..64];
    }
}
