using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Files;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services.Admin;
using JaeZoo.Server.Services.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

[ApiController]
[Authorize(Policy = AuthPolicies.ModerationAccess)]
[Route("api/admin/file-threats")]
public sealed class AdminFileThreatsController(
    AppDbContext db,
    FileModerationService moderation,
    AdminAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminFileThreatsPageDto>> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        [FromQuery] int longPendingMinutes = 10,
        CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 300);
        longPendingMinutes = Math.Clamp(longPendingMinutes, 1, 1440);

        var now = DateTime.UtcNow;
        var longPendingCutoff = now.AddMinutes(-longPendingMinutes);

        var query = db.ChatFiles.AsNoTracking()
            .Where(f =>
                f.BlockedAt != null ||
                f.IsPotentiallyDangerous ||
                f.ScanStatus == FileScanStatus.Suspicious ||
                f.ScanStatus == FileScanStatus.Failed ||
                f.ScanStatus == FileScanStatus.Blocked ||
                ((f.ScanStatus == FileScanStatus.Pending ||
                  f.ScanStatus == FileScanStatus.MetadataChecked ||
                  f.ScanStatus == FileScanStatus.NotScanned) && f.CreatedAt <= longPendingCutoff));

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(f => f.BlockedAt ?? f.DeletedAt ?? f.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(f => new
            {
                f.Id,
                f.CreatedAt,
                f.UploaderId,
                f.Kind,
                f.SizeBytes,
                f.Bucket,
                f.Sha256,
                f.ScanStatus,
                f.IsPotentiallyDangerous,
                f.RiskNote,
                f.BlockedAt,
                f.DeletedAt
            })
            .ToListAsync(ct);

        var fileIds = rows.Select(x => x.Id).ToList();
        var uploaderIds = rows.Select(x => x.UploaderId).Distinct().ToList();

        var users = await db.Users.AsNoTracking()
            .Where(u => uploaderIds.Contains(u.Id))
            .Select(u => new { u.Id, u.PublicId, u.DisplayName, u.Login })
            .ToDictionaryAsync(u => u.Id, ct);

        var directRefs = await db.DirectMessageAttachments.AsNoTracking()
            .Where(a => fileIds.Contains(a.FileId))
            .GroupBy(a => a.FileId)
            .Select(g => new { FileId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FileId, x => x.Count, ct);

        var groupRefs = await db.GroupMessageAttachments.AsNoTracking()
            .Where(a => fileIds.Contains(a.FileId))
            .GroupBy(a => a.FileId)
            .Select(g => new { FileId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FileId, x => x.Count, ct);

        var items = rows.Select(f =>
        {
            users.TryGetValue(f.UploaderId, out var u);
            directRefs.TryGetValue(f.Id, out var dm);
            groupRefs.TryGetValue(f.Id, out var gm);
            var ageMinutes = Math.Max(0, (int)Math.Round((now - f.CreatedAt).TotalMinutes));
            var locationText = dm == 0 && gm == 0 ? "не прикреплён" : $"ЛС: {dm}, группы: {gm}";

            return new AdminFileThreatDto(
                f.Id,
                f.CreatedAt,
                f.UploaderId,
                u?.PublicId ?? string.Empty,
                string.IsNullOrWhiteSpace(u?.DisplayName) ? u?.Login ?? "Удалённый пользователь" : u!.DisplayName!,
                f.Kind.ToString(),
                f.SizeBytes,
                string.IsNullOrWhiteSpace(f.Bucket) ? "jaezoo-files" : f.Bucket,
                f.Sha256 ?? string.Empty,
                f.ScanStatus.ToString(),
                f.IsPotentiallyDangerous,
                f.RiskNote,
                f.BlockedAt,
                f.DeletedAt,
                dm,
                gm,
                ageMinutes,
                locationText);
        }).ToList();

        return new AdminFileThreatsPageDto(total, items);
    }

    [HttpPost("{fileId:guid}/rescan")]
    public async Task<IActionResult> Rescan(Guid fileId, [FromBody] AdminFileThreatActionRequest? request, CancellationToken ct)
    {
        var file = await db.ChatFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound("Файл не найден.");
        if (file.DeletedAt != null || file.BlockedAt != null) return BadRequest("Удалённый или заблокированный файл нельзя отправить на повторную проверку.");

        file.ScanStatus = FileScanStatus.Pending;
        file.RiskNote = string.IsNullOrWhiteSpace(request?.Reason) ? "Повторная проверка запрошена администратором." : request!.Reason!.Trim();
        file.IsPotentiallyDangerous = false;
        await db.SaveChangesAsync(ct);
        await moderation.BroadcastFileMessagesUpdatedAsync(file.Id, ct);
        await audit.WriteAsync(User, HttpContext, "FileRescanRequested", "File", file.Id.ToString(), $"Rescan requested. Sha256={file.Sha256}", ct);
        return NoContent();
    }

    [HttpPost("{fileId:guid}/allowlist")]
    [Authorize(Policy = AuthPolicies.AdminAccess)]
    public async Task<IActionResult> AllowList(Guid fileId, [FromBody] AdminFileThreatActionRequest? request, CancellationToken ct)
    {
        var file = await db.ChatFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound("Файл не найден.");
        if (string.IsNullOrWhiteSpace(file.Sha256)) return BadRequest("У файла нет SHA-256, добавить в список ложных срабатываний нельзя.");

        var sha = file.Sha256.Trim().ToUpperInvariant();
        var existing = await db.FileScanAllowList.FirstOrDefaultAsync(a => a.Sha256 == sha, ct);
        if (existing is null)
        {
            db.FileScanAllowList.Add(new FileScanAllowList
            {
                Id = Guid.NewGuid(),
                Sha256 = sha,
                Reason = string.IsNullOrWhiteSpace(request?.Reason) ? "Ложное срабатывание подтверждено администратором." : request!.Reason!.Trim(),
                ApprovedByUserId = GetActorUserIdOrNull(),
                ApprovedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Reason = string.IsNullOrWhiteSpace(request?.Reason) ? existing.Reason : request!.Reason!.Trim();
            existing.ApprovedByUserId = GetActorUserIdOrNull();
            existing.ApprovedAt = DateTime.UtcNow;
        }

        if (file.DeletedAt == null && file.BlockedAt == null)
        {
            file.ScanStatus = FileScanStatus.Clean;
            file.IsPotentiallyDangerous = false;
            file.RiskNote = null;
        }

        await db.SaveChangesAsync(ct);
        await moderation.BroadcastFileMessagesUpdatedAsync(file.Id, ct);
        await audit.WriteAsync(User, HttpContext, "FileFalsePositiveAllowListed", "File", file.Id.ToString(), $"Allowlisted SHA-256={sha}", ct);
        return NoContent();
    }

    [HttpPost("{fileId:guid}/mark-suspicious")]
    public async Task<IActionResult> MarkSuspicious(Guid fileId, [FromBody] AdminFileThreatActionRequest? request, CancellationToken ct)
    {
        var file = await db.ChatFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound("Файл не найден.");
        if (file.DeletedAt != null || file.BlockedAt != null) return BadRequest("Файл уже удалён или заблокирован.");

        file.ScanStatus = FileScanStatus.Suspicious;
        file.IsPotentiallyDangerous = true;
        file.RiskNote = string.IsNullOrWhiteSpace(request?.Reason) ? "Файл помечен как подозрительный администратором." : request!.Reason!.Trim();
        await db.SaveChangesAsync(ct);
        await moderation.BroadcastFileMessagesUpdatedAsync(file.Id, ct);
        await audit.WriteAsync(User, HttpContext, "FileMarkedSuspicious", "File", file.Id.ToString(), $"Marked suspicious. Sha256={file.Sha256}. Reason={file.RiskNote}", ct);
        return NoContent();
    }

    [HttpDelete("{fileId:guid}")]
    public async Task<IActionResult> Delete(Guid fileId, [FromQuery] string? reason = null, CancellationToken ct = default)
    {
        var file = await db.ChatFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound("Файл не найден.");

        var note = string.IsNullOrWhiteSpace(reason) ? "Файл удалён модерацией JaeZoo." : reason.Trim();
        await moderation.RemoveDangerousFileAsync(fileId, note, ct);
        await audit.WriteAsync(User, HttpContext, "FileDeletedByModerator", "File", fileId.ToString(), $"Deleted threat file. Sha256={file.Sha256}. Reason={note}", ct);
        return NoContent();
    }

    private Guid? GetActorUserIdOrNull()
    {
        var value =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue("sub") ??
            User.FindFirstValue("nameid");
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
