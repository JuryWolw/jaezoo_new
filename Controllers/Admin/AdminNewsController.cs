using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Admin;
using JaeZoo.Server.Services.News;
using JaeZoo.Server.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

public sealed record SaveNewsRequest(
    int Category,
    string? Title,
    string? Summary,
    string? Body,
    string? CoverKey,
    string? Version,
    bool IsPinned,
    bool SendToSystemChat = false);

public sealed record AdminNewsDto(NewsItemDto Item, string? CoverKey, bool SentToSystemChat, string CreatedByName);

public sealed record NewsCoverUploadDto(string Key, string Url);

[ApiController]
[Authorize(Policy = AuthPolicies.AdminAccess)]
[Route("api/admin/news")]
public sealed class AdminNewsController(
    AppDbContext db,
    NewsService news,
    IObjectStorage storage,
    AdminAuditService audit) : ControllerBase
{
    private const long MaxCoverBytes = 5L * 1024 * 1024;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminNewsDto>>> List([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var posts = await db.NewsPosts.AsNoTracking().OrderByDescending(p => p.PublishedAt).Take(limit).ToListAsync(ct);
        return posts.Select(ToAdminDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<AdminNewsDto>> Create([FromBody] SaveNewsRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error)
            return BadRequest(error);
        if (CurrentUserId() is not { } actorId)
            return Unauthorized();

        var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actorId, ct);
        var post = new NewsPost { CreatedByUserId = actorId, CreatedByName = actor is null ? "Администратор" : Truncate(UserIdentityService.GetPublicName(actor), 64) };
        Apply(post, request);

        await news.PublishAsync(post, request.SendToSystemChat, ct);
        await audit.WriteAsync(User, HttpContext, "NewsPublished", "News", post.Id.ToString(),
            $"Новость «{post.Title}» (категория {post.Category}); в личку: {post.SentToSystemChat}", ct);
        return ToAdminDto(post);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AdminNewsDto>> Update(Guid id, [FromBody] SaveNewsRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error)
            return BadRequest(error);
        var post = await db.NewsPosts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null)
            return NotFound();

        Apply(post, request);
        post.EditedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await news.NotifyChangedAsync(post, ct);
        await audit.WriteAsync(User, HttpContext, "NewsEdited", "News", post.Id.ToString(), $"Новость «{post.Title}» изменена", ct);
        return ToAdminDto(post);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var post = await db.NewsPosts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null)
            return NotFound();

        db.NewsPosts.Remove(post);
        await db.SaveChangesAsync(ct);
        await news.NotifyDeletedAsync(id, ct);
        await audit.WriteAsync(User, HttpContext, "NewsDeleted", "News", id.ToString(), $"Новость «{post.Title}» удалена", ct);
        return NoContent();
    }

    [HttpPost("covers")]
    [RequestSizeLimit(MaxCoverBytes + 64 * 1024)]
    public async Task<ActionResult<NewsCoverUploadDto>> UploadCover(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest("Выберите картинку.");
        if (file.Length > MaxCoverBytes)
            return BadRequest("Обложка — не больше 5 МБ.");

        var ext = (file.ContentType ?? string.Empty).ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => null
        };
        if (ext is null)
            return BadRequest("Обложка — PNG, JPG или WebP.");

        var key = $"{NewsService.CoverPrefix}{Guid.NewGuid():N}{ext}";
        await using (var stream = file.OpenReadStream())
            await storage.PutAsync(key, stream, file.ContentType!, ct);

        await audit.WriteAsync(User, HttpContext, "NewsCoverUploaded", "NewsCover", key, $"Загружена обложка {key}", ct);
        return new NewsCoverUploadDto(key, NewsService.CoverUrl(key)!);
    }

    private static string? Validate(SaveNewsRequest r)
    {
        if (!NewsPost.IsKnownCategory(r.Category)) return "Неизвестная категория.";
        if (string.IsNullOrWhiteSpace(r.Title)) return "Нужен заголовок.";
        if (r.Title.Trim().Length > 160) return "Заголовок — не больше 160 символов.";
        if ((r.Summary ?? string.Empty).Trim().Length > 400) return "Анонс — не больше 400 символов.";
        if ((r.Body ?? string.Empty).Length > 20000) return "Текст — не больше 20 000 символов.";
        if ((r.Version ?? string.Empty).Trim().Length > 32) return "Версия — не больше 32 символов.";
        if (!string.IsNullOrWhiteSpace(r.CoverKey) && NewsService.CoverUrl(r.CoverKey) is null) return "Обложка не найдена — загрузите её заново.";
        return null;
    }

    private static void Apply(NewsPost post, SaveNewsRequest r)
    {
        post.Category = r.Category;
        post.Title = r.Title!.Trim();
        post.Summary = (r.Summary ?? string.Empty).Trim();
        post.Body = (r.Body ?? string.Empty).Replace("\r\n", "\n").Trim();
        post.CoverKey = string.IsNullOrWhiteSpace(r.CoverKey) ? null : r.CoverKey.Trim();
        post.Version = string.IsNullOrWhiteSpace(r.Version) ? null : r.Version.Trim();
        post.IsPinned = r.IsPinned;
    }

    private static AdminNewsDto ToAdminDto(NewsPost p) => new(NewsService.ToDto(p), p.CoverKey, p.SentToSystemChat, p.CreatedByName);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private Guid? CurrentUserId()
    {
        var idValue = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idValue, out var id) ? id : null;
    }
}
