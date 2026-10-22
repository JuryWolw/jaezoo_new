using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

/// <summary>Страницы вкладок клиента («Поддержка», «Сотрудничество», FAQ) — читают все.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/content")]
public sealed class ContentController(SiteContentService content) : ControllerBase
{
    [HttpGet("{key}")]
    public async Task<ActionResult<SiteContentDto>> Get(string key, CancellationToken ct)
    {
        try { return await content.GetAsync(key, ct); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }
}

/// <summary>Правка страниц вкладок из админки (с аудитом); клиенты получают ContentChanged и перечитывают страницу.</summary>
[ApiController]
[Authorize(Policy = AuthPolicies.AdminAccess)]
[Route("api/admin/content")]
public sealed class AdminContentController(SiteContentService content, AppDbContext db, AdminAuditService audit) : ControllerBase
{
    [HttpGet("{key}")]
    public async Task<ActionResult<SiteContentDto>> Get(string key, CancellationToken ct)
    {
        try { return await content.GetAsync(key, ct); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpPut("{key}")]
    public async Task<ActionResult<SiteContentDto>> Save(string key, [FromBody] SaveSiteContentRequest request, CancellationToken ct)
    {
        try
        {
            var editor = await EditorNameAsync(ct);
            var saved = await content.SaveAsync(key, request.Json ?? string.Empty, editor, ct);
            await audit.WriteAsync(User, HttpContext, "ContentEdited", "Content", key, $"Страница «{key}» изменена", ct);
            return saved;
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private async Task<string> EditorNameAsync(CancellationToken ct)
    {
        var sub = User.FindFirst("sub")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var id)) return "Администратор";
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? "Администратор" : UserIdentityService.GetPublicName(user);
    }
}
