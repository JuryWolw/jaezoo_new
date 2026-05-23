using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Admin;
using JaeZoo.Server.Services.Email;
using JaeZoo.Server.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

[ApiController]
[Authorize(Policy = AuthPolicies.ModerationAccess)]
[Route("api/admin/users")]
public sealed class AdminUsersController(AppDbContext db, IObjectStorage storage, AdminAuditService audit, IEmailSender emailSender, IConfiguration cfg, ILogger<AdminUsersController> log) : ControllerBase
{
    private string AvatarBucket => cfg["ObjectStorage:Buckets:Avatars"] ?? "jaezoo-avatars";

    [HttpGet]
    public async Task<ActionResult<AdminUsersPageDto>> List([FromQuery] string? q = null, [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 300);
        q = q?.Trim();

        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var nq = q.ToUpperInvariant();
            query = query.Where(u =>
                ((u.PublicId ?? string.Empty).ToUpper()).Contains(nq) ||
                ((u.DisplayName ?? string.Empty).ToUpper()).Contains(nq) ||
                ((u.Email ?? string.Empty).ToUpper()).Contains(nq) ||
                ((u.Login ?? string.Empty).ToUpper()).Contains(nq));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(u => new
            {
                u.Id,
                u.PublicId,
                u.DisplayName,
                u.Login,
                u.Email,
                u.EmailConfirmed,
                u.IsDisabled,
                u.DisabledReason,
                u.CreatedAt,
                u.LastSeen,
                u.AvatarUrl,
                u.ProfileBannerUrl
            })
            .ToListAsync(ct);

        var ids = rows.Select(x => x.Id).ToList();
        var roleRows = await db.UserRoles.AsNoTracking()
            .Where(r => ids.Contains(r.UserId) && r.RevokedAt == null)
            .Select(r => new { r.UserId, r.Role })
            .ToListAsync(ct);

        var roles = roleRows
            .GroupBy(r => r.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Role.ToString()).OrderBy(x => x).ToArray());

        var items = rows.Select(u => new AdminUserListItemDto(
            u.Id,
            u.PublicId ?? string.Empty,
            string.IsNullOrWhiteSpace(u.DisplayName) ? u.Login ?? string.Empty : u.DisplayName,
            u.Login ?? string.Empty,
            u.Email ?? string.Empty,
            u.EmailConfirmed,
            u.IsDisabled,
            u.DisabledReason,
            u.CreatedAt,
            u.LastSeen,
            $"/avatars/{u.Id}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            u.ProfileBannerUrl is null ? null : $"/api/users/{u.Id}/banner?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            roles.TryGetValue(u.Id, out var rs) ? rs : Array.Empty<string>())).ToList();

        return new AdminUsersPageDto(total, items);
    }

    [HttpDelete("{userId:guid}")]
    [Authorize(Policy = AuthPolicies.AdminAccess)]
    public async Task<IActionResult> Delete(Guid userId, [FromQuery] string? reason = null, CancellationToken ct = default)
    {
        var actorId = GetActorUserId();
        if (actorId == userId) return BadRequest("Нельзя удалить свой аккаунт из админки.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound("Пользователь не найден.");

        var isOwner = await db.UserRoles.AnyAsync(r => r.UserId == userId && r.Role == GlobalRole.Owner && r.RevokedAt == null, ct);
        if (isOwner) return BadRequest("Owner нельзя удалить через админку.");

        try
        {
            var subject = "Аккаунт JaeZoo удалён";
            var body = $"""
                  Здравствуйте, {UserIdentityService.GetPublicName(user)}.

                  Ваш аккаунт JaeZoo был удалён администрацией.
                  Причина: {(string.IsNullOrWhiteSpace(reason) ? "Удаление администратором" : reason)}

                  Это действие необратимо.
                  """;
            await emailSender.SendAccountNotificationAsync(user, subject, body, null, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to send account deletion email to {UserId}", userId);
        }

        await DeleteUserStorageAsync(userId, user.ProfileBannerUrl, ct);

        var ownedGroupIds = await db.GroupChats.AsNoTracking().Where(g => g.OwnerId == userId).Select(g => g.Id).ToListAsync(ct);
        var directDialogIds = await db.DirectDialogs.AsNoTracking().Where(d => d.User1Id == userId || d.User2Id == userId).Select(d => d.Id).ToListAsync(ct);
        var directMessageIds = await db.DirectMessages.AsNoTracking().Where(m => m.SenderId == userId || directDialogIds.Contains(m.DialogId)).Select(m => m.Id).ToListAsync(ct);
        var groupMessageIds = await db.GroupMessages.AsNoTracking().Where(m => m.SenderId == userId || ownedGroupIds.Contains(m.GroupChatId)).Select(m => m.Id).ToListAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.DirectMessageAttachments.Where(a => directMessageIds.Contains(a.MessageId)).ExecuteDeleteAsync(ct);
        await db.GroupMessageAttachments.Where(a => groupMessageIds.Contains(a.MessageId)).ExecuteDeleteAsync(ct);
        await db.DirectMessages.Where(m => directMessageIds.Contains(m.Id)).ExecuteDeleteAsync(ct);
        await db.GroupMessages.Where(m => groupMessageIds.Contains(m.Id)).ExecuteDeleteAsync(ct);
        await db.DirectDialogs.Where(d => directDialogIds.Contains(d.Id)).ExecuteDeleteAsync(ct);
        await db.GroupVoiceParticipants.Where(p => p.UserId == userId || ownedGroupIds.Contains(p.GroupChatId)).ExecuteDeleteAsync(ct);
        await db.GroupVoiceSessions.Where(s => ownedGroupIds.Contains(s.GroupChatId)).ExecuteDeleteAsync(ct);
        await db.GroupChatMembers.Where(m => m.UserId == userId || ownedGroupIds.Contains(m.GroupChatId)).ExecuteDeleteAsync(ct);
        await db.GroupAvatars.Where(a => ownedGroupIds.Contains(a.GroupChatId)).ExecuteDeleteAsync(ct);
        await db.GroupChats.Where(g => ownedGroupIds.Contains(g.Id)).ExecuteDeleteAsync(ct);
        await db.Friendships.Where(f => f.RequesterId == userId || f.AddresseeId == userId).ExecuteDeleteAsync(ct);
        await db.UserRoles.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        await db.UserSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await db.EmailVerificationCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Avatars.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.UserAvatars.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.ChatFiles.Where(f => f.UploaderId == userId).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);

        await audit.WriteAsync(User, HttpContext, "UserDeleted", "User", userId.ToString(), $"Deleted user {user.PublicId} / {UserIdentityService.GetPublicName(user)}. Reason: {reason}", ct);
        return NoContent();
    }

    private async Task DeleteUserStorageAsync(Guid userId, string? bannerUrl, CancellationToken ct)
    {
        var objects = new List<(string Bucket, string Key)>();
        var avatars = await db.UserAvatars.AsNoTracking().Where(a => a.UserId == userId && a.ObjectKey != "").Select(a => new { a.Bucket, a.ObjectKey }).ToListAsync(ct);
        objects.AddRange(avatars.Select(a => (string.IsNullOrWhiteSpace(a.Bucket) ? AvatarBucket : a.Bucket, a.ObjectKey)));
        if (TryParseStorageUrl(bannerUrl, out var bannerBucket, out var bannerKey)) objects.Add((bannerBucket, bannerKey));
        var files = await db.ChatFiles.AsNoTracking().Where(f => f.UploaderId == userId && f.ObjectKey != "").Select(f => new { f.Bucket, f.ObjectKey, f.StoredPath }).ToListAsync(ct);
        objects.AddRange(files.Select(f => (string.IsNullOrWhiteSpace(f.Bucket) ? "jaezoo-files" : f.Bucket, string.IsNullOrWhiteSpace(f.ObjectKey) ? f.StoredPath : f.ObjectKey)));

        foreach (var (bucket, key) in objects.Distinct())
        {
            if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(key)) continue;
            try { await storage.DeleteAsync(bucket, key, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Failed to delete user storage object. UserId={UserId} Bucket={Bucket} Key={Key}", userId, bucket, key); }
        }
    }

    private bool TryParseStorageUrl(string? url, out string bucket, out string key)
    {
        bucket = AvatarBucket;
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var value = url.Trim();
        if (value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[5..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || slash >= rest.Length - 1) return false;
            bucket = rest[..slash];
            key = rest[(slash + 1)..];
            return true;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        bucket = Uri.UnescapeDataString(parts[0]);
        key = Uri.UnescapeDataString(parts[1]);
        return true;
    }

    private Guid? GetActorUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
