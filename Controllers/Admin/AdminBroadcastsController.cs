using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Admin;
using JaeZoo.Server.Services.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers.Admin;

public sealed record CreateBroadcastRequest(
    string? Title,
    string? Text,
    bool ToNotifications,
    bool ToSystemChat,
    int Audience,
    bool DeliverOffline,
    int? ExpiresInHours,
    List<Guid>? FileIds,
    Guid? TargetUserId = null);

public sealed record BroadcastDto(
    Guid Id,
    DateTime CreatedAt,
    string CreatedByName,
    string Title,
    string Text,
    bool ToNotifications,
    bool ToSystemChat,
    int Audience,
    bool DeliverOffline,
    DateTime ExpiresAt,
    int RecipientsCount,
    int ImagesCount,
    Guid? TargetUserId = null,
    string? TargetName = null);

/// <summary>Событие для клиента: уведомление-рассылка в строке уведомлений.</summary>
public sealed record BroadcastNotificationDto(Guid Id, string Title, string Text, DateTime CreatedAt);

[ApiController]
[Authorize(Policy = AuthPolicies.AdminAccess)]
[Route("api/admin/broadcasts")]
public sealed class AdminBroadcastsController(
    AppDbContext db,
    AdminAuditService audit,
    IHubContext<ChatHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<AdminBroadcastsController> logger) : ControllerBase
{
    public const string HubEventName = "SystemBroadcast";
    private static readonly TimeSpan MaxOfflineWindow = TimeSpan.FromDays(7);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BroadcastDto>>> List([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var items = await db.AdminBroadcasts.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
        return items.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<BroadcastDto>> Create([FromBody] CreateBroadcastRequest request, CancellationToken ct)
    {
        var actorId = GetActorUserId();
        if (actorId is null)
            return Unauthorized();

        var title = (request.Title ?? string.Empty).Trim();
        var text = (request.Text ?? string.Empty).Trim();
        if (title.Length > 120) title = title[..120];
        if (text.Length > 4000) return BadRequest("Текст рассылки — не больше 4000 символов.");
        if (!request.ToNotifications && !request.ToSystemChat) return BadRequest("Выберите, куда отправить рассылку.");
        if (request.Audience is not (AdminBroadcast.AudienceAll or AdminBroadcast.AudienceStaff or AdminBroadcast.AudienceUser)) return BadRequest("Неизвестная аудитория.");

        User? target = null;
        if (request.Audience == AdminBroadcast.AudienceUser)
        {
            if (request.TargetUserId is not { } targetId || targetId == Guid.Empty || SystemChatService.IsSystemUser(targetId))
                return BadRequest("Выберите пользователя.");
            target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetId, ct);
            if (target is null) return BadRequest("Пользователь не найден.");
        }

        var fileIds = (request.FileIds ?? new()).Where(x => x != Guid.Empty).Distinct().ToList();
        if (fileIds.Count > 10) return BadRequest("Не больше 10 картинок.");
        if (fileIds.Count > 0 && !request.ToSystemChat) return BadRequest("Картинки можно отправить только в системный чат.");
        if (string.IsNullOrWhiteSpace(text) && fileIds.Count == 0) return BadRequest("Добавьте текст или картинку.");
        if (request.ToNotifications && string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(title))
            return BadRequest("Для уведомления нужен заголовок или текст.");

        List<ChatFile> files = new();
        if (fileIds.Count > 0)
        {
            files = await db.ChatFiles.Where(f => fileIds.Contains(f.Id)).ToListAsync(ct);
            if (files.Count != fileIds.Count) return BadRequest("Картинка не найдена — загрузите её заново.");
            foreach (var f in files)
            {
                if (f.UploaderId != actorId.Value) return BadRequest("Можно прикрепить только свои загруженные картинки.");
                if (f.BlockedAt.HasValue || f.DeletedAt.HasValue) return BadRequest("Картинка заблокирована или удалена.");
                if (!(f.ContentType ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return BadRequest("В рассылку можно прикрепить только картинки.");
            }
        }

        var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actorId.Value, ct);
        var now = DateTime.UtcNow;
        var hours = Math.Clamp(request.ExpiresInHours ?? 72, 1, (int)MaxOfflineWindow.TotalHours);

        var recipients = target is not null
            ? new List<Guid> { target.Id }
            : await ResolveAudienceAsync(db, request.Audience, ct);

        var broadcast = new AdminBroadcast
        {
            CreatedAt = now,
            CreatedByUserId = actorId.Value,
            CreatedByName = actor is null ? "Администратор" : UserIdentityService.GetPublicName(actor),
            Title = title,
            Text = text,
            Channels = (request.ToNotifications ? AdminBroadcast.ChannelNotifications : 0) | (request.ToSystemChat ? AdminBroadcast.ChannelSystemChat : 0),
            Audience = request.Audience,
            DeliverOffline = request.ToNotifications && request.DeliverOffline,
            ExpiresAt = now.AddHours(hours),
            RecipientsCount = recipients.Count,
            TargetUserId = target?.Id,
            TargetName = target is null ? null : Truncate(UserIdentityService.GetPublicName(target), 64),
            FileIdsCsv = fileIds.Count == 0 ? null : string.Join(',', fileIds)
        };
        db.AdminBroadcasts.Add(broadcast);

        // Файлы рассылки становятся «прикреплёнными»: доступ к ним дальше — по участию в системном диалоге.
        foreach (var f in files)
        {
            f.IsAttached = true;
            f.AttachedAt = now;
        }

        await db.SaveChangesAsync(ct);

        if (request.ToNotifications)
        {
            var payload = new BroadcastNotificationDto(broadcast.Id, broadcast.Title, broadcast.Text, broadcast.CreatedAt);
            if (request.Audience == AdminBroadcast.AudienceAll)
                await hub.Clients.All.SendAsync(HubEventName, payload, ct);
            else
                await hub.Clients.Users(recipients.Select(x => x.ToString()).ToList()).SendAsync(HubEventName, payload, ct);
        }

        if (request.ToSystemChat)
        {
            var chatText = string.IsNullOrWhiteSpace(title) ? text : string.IsNullOrWhiteSpace(text) ? title : $"{title}\n\n{text}";
            _ = Task.Run(() => DeliverToSystemChatAsync(scopeFactory, logger, recipients, chatText, fileIds));
        }

        var where = string.Join(" + ", new[] { request.ToNotifications ? "уведомления" : null, request.ToSystemChat ? "системный чат" : null }.Where(x => x != null));
        await audit.WriteAsync(User, HttpContext, "BroadcastSent", "Broadcast", broadcast.Id.ToString(),
            $"Рассылка «{(title.Length > 0 ? title : text.Length > 60 ? text[..60] + "…" : text)}» → {where}; аудитория: {(target is not null ? $"{target.PublicId} / {broadcast.TargetName}" : request.Audience == AdminBroadcast.AudienceAll ? "все" : "персонал")}; получателей: {recipients.Count}; офлайн: {broadcast.DeliverOffline}; картинок: {fileIds.Count}", ct);

        return ToDto(broadcast);
    }

    internal static async Task<List<Guid>> ResolveAudienceAsync(AppDbContext db, int audience, CancellationToken ct)
    {
        if (audience == AdminBroadcast.AudienceStaff)
        {
            return await db.UserRoles.AsNoTracking()
                .Where(r => r.RevokedAt == null && r.Role != GlobalRole.User)
                .Select(r => r.UserId)
                .Distinct()
                .ToListAsync(ct);
        }

        return await db.Users.AsNoTracking()
            .Where(u => !u.IsDisabled && u.Id != SystemChatService.SystemUserId)
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    internal static async Task DeliverToSystemChatAsync(IServiceScopeFactory scopeFactory, ILogger logger, List<Guid> recipients, string text, List<Guid> fileIds)
    {
        var delivered = 0;
        foreach (var chunk in recipients.Chunk(50))
        {
            // Новый scope на пачку: DbContext не разрастается от тысяч отслеживаемых сущностей.
            using var scope = scopeFactory.CreateScope();
            var systemChat = scope.ServiceProvider.GetRequiredService<SystemChatService>();
            foreach (var userId in chunk)
            {
                try
                {
                    if (await systemChat.PostAsync(userId, SystemChatService.BroadcastKey, text, fileIds) is not null)
                        delivered++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Broadcast to system chat failed for one user.");
                }
            }
        }

        logger.LogInformation("Broadcast delivered to system chat: {Delivered}/{Total}.", delivered, recipients.Count);
    }

    private static BroadcastDto ToDto(AdminBroadcast x) => new(
        x.Id,
        x.CreatedAt,
        x.CreatedByName,
        x.Title,
        x.Text,
        (x.Channels & AdminBroadcast.ChannelNotifications) != 0,
        (x.Channels & AdminBroadcast.ChannelSystemChat) != 0,
        x.Audience,
        x.DeliverOffline,
        x.ExpiresAt,
        x.RecipientsCount,
        string.IsNullOrWhiteSpace(x.FileIdsCsv) ? 0 : x.FileIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Length,
        x.TargetUserId,
        x.TargetName);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private Guid? GetActorUserId()
    {
        var idValue = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idValue, out var id) ? id : null;
    }
}

/// <summary>Для клиента: уведомления-рассылки, которые нужно показать при входе (отправлены, пока пользователь был не в сети).</summary>
[ApiController]
[Authorize]
[Route("api/broadcasts")]
public sealed class BroadcastsController(AppDbContext db) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<ActionResult<IReadOnlyList<BroadcastNotificationDto>>> Pending([FromQuery] DateTime? sinceUtc, CancellationToken ct)
    {
        var idValue = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idValue, out var me))
            return Unauthorized();

        var now = DateTime.UtcNow;
        var since = sinceUtc.HasValue ? DateTime.SpecifyKind(sinceUtc.Value, DateTimeKind.Utc) : now.AddDays(-7);
        var isStaff = await db.UserRoles.AsNoTracking().AnyAsync(r => r.UserId == me && r.RevokedAt == null && r.Role != GlobalRole.User, ct);

        var items = await db.AdminBroadcasts.AsNoTracking()
            .Where(x => (x.Channels & AdminBroadcast.ChannelNotifications) != 0
                        && x.DeliverOffline
                        && x.ExpiresAt > now
                        && x.CreatedAt > since
                        && (x.Audience == AdminBroadcast.AudienceAll
                            || (isStaff && x.Audience == AdminBroadcast.AudienceStaff)
                            || (x.Audience == AdminBroadcast.AudienceUser && x.TargetUserId == me)))
            .OrderBy(x => x.CreatedAt)
            .Take(20)
            .Select(x => new BroadcastNotificationDto(x.Id, x.Title, x.Text, x.CreatedAt))
            .ToListAsync(ct);

        return items;
    }
}
