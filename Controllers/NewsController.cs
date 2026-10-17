using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using JaeZoo.Server.Data;
using JaeZoo.Server.Services.News;
using JaeZoo.Server.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

public sealed record MarkNewsSeenRequest(DateTime UpTo);

/// <summary>Лента новостей для клиента и отметка «просмотрено» (одна на пользователя — синхронно на всех устройствах).</summary>
[ApiController]
[Authorize]
[Route("api/news")]
public sealed partial class NewsController(AppDbContext db, IObjectStorage storage) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NewsFeedDto>> Feed([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (CurrentUserId() is not { } me)
            return Unauthorized();

        limit = Math.Clamp(limit, 1, 100);
        var posts = await db.NewsPosts.AsNoTracking()
            .OrderByDescending(p => p.PublishedAt)
            .Take(limit)
            .ToListAsync(ct);
        var lastSeen = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.LastSeenNewsAt).FirstOrDefaultAsync(ct);
        return new NewsFeedDto(posts.Select(NewsService.ToDto).ToList(), lastSeen);
    }

    [HttpPost("seen")]
    public async Task<IActionResult> Seen([FromBody] MarkNewsSeenRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } me)
            return Unauthorized();

        var now = DateTime.UtcNow;
        var upTo = DateTime.SpecifyKind(request.UpTo, DateTimeKind.Utc);
        if (upTo > now) upTo = now;

        // Только вперёд: старое устройство не вернёт точки «новое».
        await db.Users
            .Where(u => u.Id == me && (u.LastSeenNewsAt == null || u.LastSeenNewsAt < upTo))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenNewsAt, upTo), ct);
        return NoContent();
    }

    /// <summary>Обложки — публичные картинки со случайным именем, кешируются навсегда.</summary>
    [AllowAnonymous]
    [HttpGet("covers/{name}")]
    public async Task<IActionResult> Cover(string name, CancellationToken ct)
    {
        if (!CoverNameRegex().IsMatch(name))
            return NotFound();
        try
        {
            var (stream, contentType, _) = await storage.GetAsync(NewsService.CoverPrefix + name, ct);
            Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return File(stream, contentType);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return NotFound();
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}\\.(png|jpg|webp)$")]
    internal static partial Regex CoverNameRegex();

    private Guid? CurrentUserId()
    {
        var idValue = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idValue, out var id) ? id : null;
    }
}
