using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Files;
using JaeZoo.Server.Services.Chat;
using JaeZoo.Server.Services.Files;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ChatController(
    AppDbContext db,
    ILogger<ChatController> log,
    DirectChatService chat,
    GroupChatService groupChats,
    FileCleanupService fileCleanup,
    IHubContext<ChatHub> hub,
    IWebHostEnvironment env) : ControllerBase
{
    private Guid MeId
    {
        get
        {
            var s = User.FindFirst("sub")?.Value
                    ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst("uid")?.Value;

            if (!Guid.TryParse(s, out var id))
                throw new UnauthorizedAccessException("No user id claim.");

            return id;
        }
    }

    private string GetGroupDefaultAvatarPath()
    {
        var root = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var groupDefault = Path.Combine(root, "avatars", "default_group.png");
        if (System.IO.File.Exists(groupDefault))
            return groupDefault;

        return Path.Combine(root, "avatars", "default.png");
    }

    private async Task<bool> IsEmailConfirmedAsync(Guid userId, CancellationToken ct)
        => await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.EmailConfirmed)
            .FirstOrDefaultAsync(ct);

    private async Task<ActionResult?> RequireUsersVerifiedAsync(IEnumerable<Guid> userIds, string message, CancellationToken ct)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return null;

        var confirmedCount = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.EmailConfirmed)
            .CountAsync(ct);

        if (confirmedCount == ids.Count) return null;

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = "email_not_verified",
            message
        });
    }

    private async Task BroadcastGroupSummaryChanged(Guid groupId, CancellationToken ct)
    {
        var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
        if (summary is not null)
            await BroadcastGroupUpdated(summary, ct);
    }

    private static string CleanSystemMessageText(string? value, int maxLength = 500)
    {
        var text = (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        if (text.Length > maxLength)
            text = text[..maxLength].Trim();

        return text;
    }

    private static bool IsKnownGroupSystemKey(string? systemKey) => systemKey is
        GroupChatService.SystemUserAddedKey or
        GroupChatService.SystemHistoryAvailableKey or
        GroupChatService.SystemSecurityKeysUpdatedKey or
        GroupChatService.SystemGroupCallStartedKey or
        GroupChatService.SystemGroupCallEndedKey;

    private async Task<bool> CanCreateGroupSystemMessageAsync(Guid groupId, Guid userId, CancellationToken ct)
    {
        var group = await db.GroupChats.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null) return false;

        var member = await db.GroupChatMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == userId, ct);
        if (member is null) return false;

        return group.OwnerId == userId || member.Role == GroupChatRole.Admin;
    }

    private async Task<MessageDto?> CreateAndBroadcastGroupSystemMessageAsync(
        Guid groupId,
        Guid senderId,
        string systemKey,
        string text,
        CancellationToken ct)
    {
        if (groupId == Guid.Empty || senderId == Guid.Empty || !IsKnownGroupSystemKey(systemKey))
            return null;

        var cleanText = CleanSystemMessageText(text);
        if (string.IsNullOrWhiteSpace(cleanText))
            return null;

        var created = await groupChats.CreateSystemMessageAsync(senderId, groupId, systemKey, cleanText, ct);
        var dto = await groupChats.GetMessageDtoAsync(groupId, created.message.Id, ct);
        if (dto is null) return null;

        await BroadcastGroupCreated(groupId, dto, ct);
        return dto;
    }

    private async Task<string> GetPublicUserNameAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? "Пользователь" : UserIdentityService.GetPublicName(user);
    }

    private async Task<Dictionary<Guid, string>> GetPublicUserNamesAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToListAsync(ct);

        return users.ToDictionary(u => u.Id, u => UserIdentityService.GetPublicName(u));
    }

    [HttpGet("history/{friendId:guid}")]
    public async Task<ActionResult<IEnumerable<MessageDto>>> History(
        Guid friendId,
        int skip = 0,
        int take = 50,
        DateTime? before = null,
        Guid? beforeId = null,
        DateTime? after = null,
        Guid? afterId = null,
        CancellationToken ct = default
    )
    {
        try
        {
            var friendExists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == friendId, ct);
            if (!friendExists) return NotFound(new { error = "Friend not found." });

            if (!await chat.AreFriends(MeId, friendId, ct)) return Forbid();

            DirectDialog dlg;
            try
            {
                dlg = await chat.GetOrCreateDialogAsync(MeId, friendId, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "GetOrCreateDialog failed: me={MeId}, friend={FriendId}", MeId, friendId);
                return Ok(Array.Empty<MessageDto>());
            }

            var q = db.DirectMessages
                .AsNoTracking()
                .Where(m => m.DialogId == dlg.Id);

            if (before.HasValue)
            {
                var bt = DirectChatService.EnsureUtc(before.Value);
                if (beforeId.HasValue)
                {
                    var bid = beforeId.Value;
                    q = q.Where(m => m.SentAt < bt || (m.SentAt == bt && m.Id.CompareTo(bid) < 0));
                }
                else
                {
                    q = q.Where(m => m.SentAt < bt);
                }
            }

            if (after.HasValue)
            {
                var at = DirectChatService.EnsureUtc(after.Value);
                if (afterId.HasValue)
                {
                    var aid = afterId.Value;
                    q = q.Where(m => m.SentAt > at || (m.SentAt == at && m.Id.CompareTo(aid) > 0));
                }
                else
                {
                    q = q.Where(m => m.SentAt > at);
                }
            }

            List<DirectMessage> rows;
            var normalizedTake = Math.Clamp(take, 1, 200);

            if (before.HasValue && !after.HasValue)
            {
                rows = await q
                    .OrderByDescending(m => m.SentAt)
                    .ThenByDescending(m => m.Id)
                    .Take(normalizedTake)
                    .ToListAsync(ct);

                rows = rows
                    .OrderBy(m => m.SentAt)
                    .ThenBy(m => m.Id)
                    .ToList();
            }
            else
            {
                rows = await q
                    .OrderBy(m => m.SentAt)
                    .ThenBy(m => m.Id)
                    .Skip(Math.Max(0, skip))
                    .Take(normalizedTake)
                    .ToListAsync(ct);
            }

            return Ok(await chat.BuildMessageDtosAsync(rows, ct));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "History failed: me={MeId}, friend={FriendId}", MeId, friendId);
            return Ok(Array.Empty<MessageDto>());
        }
    }

    [HttpGet("unread")]
    public async Task<ActionResult<IEnumerable<UnreadDialogDto>>> UnreadSummary(CancellationToken ct)
    {
        try
        {
            var me = MeId;

            var friendIds = await db.Friendships
                .AsNoTracking()
                .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == me || f.AddresseeId == me))
                .Select(f => f.RequesterId == me ? f.AddresseeId : f.RequesterId)
                .Distinct()
                .ToListAsync(ct);

            var result = new List<UnreadDialogDto>(friendIds.Count);

            foreach (var friendId in friendIds)
            {
                var friendExists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == friendId, ct);
                if (!friendExists)
                {
                    result.Add(new UnreadDialogDto(friendId, 0, null, null));
                    continue;
                }

                DirectDialog dlg;
                try
                {
                    dlg = await chat.GetOrCreateDialogAsync(me, friendId, ct);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "GetOrCreateDialog failed in UnreadSummary: me={MeId}, friend={FriendId}", me, friendId);
                    result.Add(new UnreadDialogDto(friendId, 0, null, null));
                    continue;
                }

                var (count, firstId, firstAt) = await chat.GetUnreadForUserAsync(dlg, me, ct);
                var (_, lastReadByFriendId) = DirectChatService.GetReadCursor(dlg, friendId);
                result.Add(new UnreadDialogDto(friendId, count, firstId, firstAt)
                {
                    LastReadByFriendMessageId = lastReadByFriendId == Guid.Empty ? null : lastReadByFriendId
                });
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "UnreadSummary failed: me={MeId}", MeId);
            return Ok(Array.Empty<UnreadDialogDto>());
        }
    }

    [HttpPost("send/{friendId:guid}")]
    [EnableRateLimiting("chat-write")]
    [RequireVerifiedEmail]
    [RequireRiskCaptcha("direct-message", 12, 30)]
    public async Task<ActionResult<MessageDto>> SendMessage(Guid friendId, [FromBody] SendMessageRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });
            if (!await chat.AreFriends(MeId, friendId, ct)) return Forbid();
            var peerVerified = await IsEmailConfirmedAsync(friendId, ct);
            if (!peerVerified) return StatusCode(StatusCodes.Status403Forbidden, new { code = "email_not_verified", message = "Собеседник ещё не подтвердил почту. Переписка пока недоступна." });

            var created = await chat.CreateMessageAsync(
                MeId,
                friendId,
                body.Text,
                body.FileIds,
                DirectMessageKind.User,
                null,
                null,
                ct);

            var dto = await chat.GetMessageDtoAsync(created.dialog.Id, created.message.Id, ct);
            if (dto is null) return Problem("Failed to build message dto.", statusCode: 500);

            await BroadcastCreated(friendId, dto, ct);
            return Ok(dto);
        }
        catch (DbUpdateException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            log.LogError(ex, "SendMessage db update failed: me={MeId}, friend={FriendId}, detail={Detail}", MeId, friendId, detail);
            return BadRequest(new { error = detail });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SendMessage failed: me={MeId}, friend={FriendId}", MeId, friendId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("mark-read/{friendId:guid}")]
    public async Task<IActionResult> MarkRead(Guid friendId, [FromBody] MarkReadRequest body, CancellationToken ct)
    {
        try
        {
            var me = MeId;

            var friendExists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == friendId, ct);
            if (!friendExists) return NotFound(new { error = "Friend not found." });
            if (!await chat.AreFriends(me, friendId, ct)) return Forbid();
            if (body is null) return BadRequest(new { error = "Body is required." });
            if (body.LastReadMessageId == Guid.Empty) return BadRequest(new { error = "LastReadMessageId is required." });

            DirectDialog dlg;
            try
            {
                dlg = await chat.GetOrCreateDialogAsync(me, friendId, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "GetOrCreateDialog failed in MarkRead: me={MeId}, friend={FriendId}", me, friendId);
                return Ok(new { ok = true });
            }

            var cursorMessage = await db.DirectMessages
                .AsNoTracking()
                .Where(m => m.DialogId == dlg.Id && m.Id == body.LastReadMessageId)
                .Select(m => new { m.Id, m.SentAt })
                .FirstOrDefaultAsync(ct);

            if (cursorMessage is null)
                return BadRequest(new { error = "Cursor message was not found in this dialog." });

            var at = DirectChatService.EnsureUtc(cursorMessage.SentAt);
            var mid = cursorMessage.Id;
            var (curAt, curId) = DirectChatService.GetReadCursor(dlg, me);

            if (at > curAt || (at == curAt && mid.CompareTo(curId) > 0))
            {
                DirectChatService.SetReadCursor(dlg, me, at, mid);
                await db.SaveChangesAsync(ct);
            }

            var unread = await chat.GetUnreadForUserAsync(dlg, me, ct);
            var unreadDto = new ChatUnreadChangedDto(friendId, unread.count, unread.firstId, unread.firstAt);
            await hub.Clients.User(me.ToString()).SendAsync("ChatUnreadChanged", unreadDto, ct);

            var readDto = new ChatMessageReadDto(me, me, mid, DateTime.UtcNow);
            await hub.Clients.User(friendId.ToString()).SendAsync("ChatMessageRead", readDto, ct);
            await hub.Clients.User(me.ToString()).SendAsync("ChatMessageRead", readDto, ct);

            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "MarkRead failed: me={MeId}, friend={FriendId}", MeId, friendId);
            return Ok(new { ok = true });
        }
    }

    [HttpPost("system/{friendId:guid}")]
    public async Task<ActionResult<MessageDto>> SendSystemMessage(Guid friendId, [FromBody] SendSystemMessageRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.SystemKey))
                return BadRequest(new { error = "SystemKey is required." });

            if (!await chat.AreFriends(MeId, friendId, ct)) return Forbid();
            var peerVerified = await IsEmailConfirmedAsync(friendId, ct);
            if (!peerVerified) return StatusCode(StatusCodes.Status403Forbidden, new { code = "email_not_verified", message = "Собеседник ещё не подтвердил почту. Переписка пока недоступна." });

            var created = await chat.CreateMessageAsync(MeId, friendId, body.Text, null, DirectMessageKind.System, body.SystemKey, null, ct);
            var dto = await chat.GetMessageDtoAsync(created.dialog.Id, created.message.Id, ct);
            if (dto is null) return Problem("Failed to build message dto.", statusCode: 500);

            await BroadcastCreated(friendId, dto, ct);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SendSystemMessage failed: me={MeId}, friend={FriendId}", MeId, friendId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("messages/{messageId:guid}/edit")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<MessageDto>> EditMessage(Guid messageId, [FromBody] EditMessageRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });
            var newText = (body.Text ?? string.Empty).Trim();

            var msg = await db.DirectMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
            if (msg is null) return NotFound(new { error = "Message not found." });
            if (msg.SenderId != MeId) return Forbid();
            if (msg.DeletedAt.HasValue) return BadRequest(new { error = "Deleted message cannot be edited." });
            if (msg.Kind != DirectMessageKind.User) return BadRequest(new { error = "Only user messages can be edited." });

            var hasAttachments = await db.DirectMessageAttachments.AsNoTracking().AnyAsync(a => a.MessageId == msg.Id, ct);
            if (string.IsNullOrWhiteSpace(newText) && !hasAttachments)
                return BadRequest(new { error = "Text is required when message has no attachments." });

            if (string.Equals(msg.Text, newText, StringComparison.Ordinal))
            {
                var same = await chat.GetMessageDtoAsync(msg.DialogId, msg.Id, ct);
                return Ok(same);
            }

            msg.Text = newText;
            msg.EditedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var dialog = await db.DirectDialogs.AsNoTracking().FirstAsync(d => d.Id == msg.DialogId, ct);
            var peerId = dialog.User1Id == MeId ? dialog.User2Id : dialog.User1Id;
            var dto = await chat.GetMessageDtoAsync(msg.DialogId, msg.Id, ct);
            if (dto is null) return NotFound();

            await hub.Clients.User(MeId.ToString()).SendAsync("ChatMessageUpdated", new ChatMessageUpdatedDto(peerId, dto), ct);
            await hub.Clients.User(peerId.ToString()).SendAsync("ChatMessageUpdated", new ChatMessageUpdatedDto(MeId, dto), ct);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "EditMessage failed: me={MeId}, message={MessageId}", MeId, messageId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("messages/{messageId:guid}/delete")]
    [RequireVerifiedEmail]
    public async Task<IActionResult> DeleteMessage(Guid messageId, CancellationToken ct)
    {
        try
        {
            var msg = await db.DirectMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
            if (msg is null) return NotFound(new { error = "Message not found." });
            if (msg.SenderId != MeId) return Forbid();
            if (msg.DeletedAt.HasValue) return Ok(new { ok = true });

            var attachedFileIds = await db.DirectMessageAttachments
                .AsNoTracking()
                .Where(a => a.MessageId == msg.Id)
                .Select(a => a.FileId)
                .ToListAsync(ct);

            msg.DeletedAt = DateTime.UtcNow;
            msg.DeletedById = MeId;
            msg.EditedAt = null;
            msg.Text = string.Empty;
            msg.SystemKey = null;
            await db.SaveChangesAsync(ct);
            await fileCleanup.DeleteFilesForMessageAsync(attachedFileIds, ct);

            var dialog = await db.DirectDialogs.AsNoTracking().FirstAsync(d => d.Id == msg.DialogId, ct);
            var peerId = dialog.User1Id == MeId ? dialog.User2Id : dialog.User1Id;
            var payloadMine = new ChatMessageDeletedDto(peerId, msg.Id, msg.DeletedAt.Value, MeId);
            var payloadPeer = new ChatMessageDeletedDto(MeId, msg.Id, msg.DeletedAt.Value, MeId);
            await hub.Clients.User(MeId.ToString()).SendAsync("ChatMessageDeleted", payloadMine, ct);
            await hub.Clients.User(peerId.ToString()).SendAsync("ChatMessageDeleted", payloadPeer, ct);
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DeleteMessage failed: me={MeId}, message={MessageId}", MeId, messageId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("forward/{friendId:guid}")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<IEnumerable<MessageDto>>> ForwardMessages(Guid friendId, [FromBody] ForwardMessagesRequest body, CancellationToken ct)
    {
        try
        {
            if (body?.MessageIds is null || body.MessageIds.Count == 0)
                return BadRequest(new { error = "MessageIds are required." });
            if (!await chat.AreFriends(MeId, friendId, ct)) return Forbid();
            var peerVerified = await IsEmailConfirmedAsync(friendId, ct);
            if (!peerVerified) return StatusCode(StatusCodes.Status403Forbidden, new { code = "email_not_verified", message = "Собеседник ещё не подтвердил почту. Пересылка пока недоступна." });

            var ids = body.MessageIds.Where(x => x != Guid.Empty).Distinct().Take(20).ToList();
            var sources = await (
                from m in db.DirectMessages.AsNoTracking()
                join d in db.DirectDialogs.AsNoTracking() on m.DialogId equals d.Id
                where ids.Contains(m.Id)
                      && m.DeletedAt == null
                      && (d.User1Id == MeId || d.User2Id == MeId)
                orderby m.SentAt, m.Id
                select m
            ).ToListAsync(ct);

            if (sources.Count == 0)
                return Ok(Array.Empty<MessageDto>());

            var createdDtos = new List<MessageDto>();
            foreach (var source in sources)
            {
                var forwarded = await chat.ForwardMessageAsync(
                    MeId,
                    friendId,
                    source,
                    body.IncludeAttachments,
                    ct);

                var dto = await chat.GetMessageDtoAsync(forwarded.dialog.Id, forwarded.message.Id, ct);
                if (dto is not null)
                {
                    createdDtos.Add(dto);
                    await BroadcastCreated(friendId, dto, ct);
                }
            }

            return Ok(createdDtos);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ForwardMessages failed: me={MeId}, friend={FriendId}", MeId, friendId);
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task BroadcastCreated(Guid peerId, MessageDto dto, CancellationToken ct)
    {
        await hub.Clients.User(MeId.ToString()).SendAsync("ChatMessageCreated", new ChatRealtimeMessageDto(peerId, dto), ct);
        await hub.Clients.User(peerId.ToString()).SendAsync("ChatMessageCreated", new ChatRealtimeMessageDto(MeId, dto), ct);

        var dlg = await chat.GetOrCreateDialogAsync(MeId, peerId, ct);
        var unread = await chat.GetUnreadForUserAsync(dlg, peerId, ct);
        var typedUnread = new ChatUnreadChangedDto(MeId, unread.count, unread.firstId, unread.firstAt);

        await hub.Clients.User(peerId.ToString()).SendAsync("ChatUnreadChanged", typedUnread, ct);
    }


    [HttpGet("groups")]
    public async Task<ActionResult<IEnumerable<GroupChatSummaryDto>>> ListGroups(CancellationToken ct)
    {
        try
        {
            return Ok(await groupChats.ListForUserAsync(MeId, ct));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ListGroups failed: me={MeId}", MeId);
            return Ok(Array.Empty<GroupChatSummaryDto>());
        }
    }


    [HttpGet("groups/search")]
    [EnableRateLimiting("search")]
    public async Task<ActionResult<IEnumerable<PublicGroupSearchDto>>> SearchPublicGroups([FromQuery] string? q, int take = 30, CancellationToken ct = default)
    {
        try
        {
            var query = (q ?? string.Empty).Trim();
            if (query.Length < 2)
                return Ok(Array.Empty<PublicGroupSearchDto>());

            return Ok(await groupChats.SearchPublicAsync(MeId, query, take, ct));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SearchPublicGroups failed: me={MeId}, query={Query}", MeId, q);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/{groupId:guid}/join")]
    [RequireVerifiedEmail]
    [EnableRateLimiting("security-sensitive")]
    public async Task<ActionResult<GroupChatDetailsDto>> JoinPublicGroup(Guid groupId, CancellationToken ct)
    {
        try
        {
            var wasMember = await db.GroupChatMembers.AsNoTracking()
                .AnyAsync(m => m.GroupChatId == groupId && m.UserId == MeId, ct);

            var chatEntity = await groupChats.JoinPublicAsync(groupId, MeId, ct);
            var details = await BuildGroupDetailsAsync(chatEntity.Id, ct);
            if (details is null) return Problem("Failed to build group chat dto.", statusCode: 500);

            await BroadcastGroupMembersChanged(chatEntity.Id, ct);
            await BroadcastGroupUpdated(details.Chat, ct);

            if (!wasMember)
            {
                var joinedName = await GetPublicUserNameAsync(MeId, ct);
                await CreateAndBroadcastGroupSystemMessageAsync(
                    chatEntity.Id,
                    MeId,
                    GroupChatService.SystemUserAddedKey,
                    $"Пользователь {joinedName} добавлен в группу.",
                    ct);
                await CreateAndBroadcastGroupSystemMessageAsync(
                    chatEntity.Id,
                    MeId,
                    GroupChatService.SystemSecurityKeysUpdatedKey,
                    "Ключи безопасности группы обновлены.",
                    ct);
            }

            var memberIds = await GetGroupMemberIdsAsync(chatEntity.Id, ct);
            foreach (var memberId in memberIds)
            {
                var summary = await groupChats.GetSummaryAsync(chatEntity.Id, memberId, ct);
                if (summary is not null)
                    await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatUpdated", new GroupChatUpdatedDto(summary), ct);
            }

            return Ok(details);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "JoinPublicGroup failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("groups/unread")]
    public async Task<ActionResult<IEnumerable<GroupUnreadChatDto>>> GroupUnreadSummary(CancellationToken ct)
    {
        try
        {
            var groups = await db.GroupChatMembers
                .AsNoTracking()
                .Where(m => m.UserId == MeId)
                .Select(m => m.GroupChatId)
                .Distinct()
                .ToListAsync(ct);

            var result = new List<GroupUnreadChatDto>(groups.Count);
            foreach (var groupId in groups)
            {
                var unread = await groupChats.GetUnreadForUserAsync(groupId, MeId, ct);
                var visibleMessages = VisibleGroupMessagesForMe(groupId);
                var lastReadByOtherMessageId = await db.GroupChatMembers
                    .AsNoTracking()
                    .Where(m => m.GroupChatId == groupId && m.UserId != MeId && m.LastReadMessageId != Guid.Empty)
                    .Join(visibleMessages,
                        member => member.LastReadMessageId,
                        message => message.Id,
                        (member, message) => new { member.LastReadMessageId, message.SentAt })
                    .OrderByDescending(x => x.SentAt)
                    .Select(x => (Guid?)x.LastReadMessageId)
                    .FirstOrDefaultAsync(ct);

                result.Add(new GroupUnreadChatDto(groupId, unread.count, unread.firstId, unread.firstAt)
                {
                    LastReadByOtherMessageId = lastReadByOtherMessageId
                });
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "GroupUnreadSummary failed: me={MeId}", MeId);
            return Ok(Array.Empty<GroupUnreadChatDto>());
        }
    }

    [HttpPost("groups")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<GroupChatDetailsDto>> CreateGroup([FromBody] CreateGroupChatRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });

            var memberIds = body.MemberIds ?? Array.Empty<Guid>();
            var verifyMembers = await RequireUsersVerifiedAsync(memberIds, "В группу можно добавлять только пользователей с подтверждённой почтой.", ct);
            if (verifyMembers is not null) return verifyMembers;

            var group = await groupChats.CreateChatAsync(MeId, body.Title, body.Description, body.MemberIds, body.IsPublic, body.HistoryPolicy, ct);
            var details = await BuildGroupDetailsAsync(group.Id, ct);
            if (details is null) return Problem("Failed to build group chat dto.", statusCode: 500);

            await BroadcastGroupUpdated(details.Chat, ct);
            await BroadcastGroupMembersChanged(group.Id, ct);
            return Ok(details);
        }
        catch (DbUpdateException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            log.LogError(ex, "CreateGroup db update failed: me={MeId}, detail={Detail}", MeId, detail);
            return BadRequest(new { error = detail });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "CreateGroup failed: me={MeId}", MeId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("groups/{groupId:guid}")]
    public async Task<ActionResult<GroupChatDetailsDto>> GetGroup(Guid groupId, CancellationToken ct)
    {
        try
        {
            var details = await BuildGroupDetailsAsync(groupId, ct);
            if (details is null) return NotFound(new { error = "Group chat not found." });
            return Ok(details);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "GetGroup failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/{groupId:guid}/title")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<GroupChatSummaryDto>> UpdateGroupTitle(Guid groupId, [FromBody] UpdateGroupChatRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });

            await groupChats.UpdateChatAsync(groupId, MeId, body.Title, body.Description, body.IsPublic, body.HistoryPolicy, ct);
            var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
            if (summary is null) return NotFound(new { error = "Group chat not found." });

            await BroadcastGroupUpdated(summary, ct);
            return Ok(summary);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "UpdateGroupMeta failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/{groupId:guid}/members")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<IReadOnlyList<GroupChatMemberDto>>> AddGroupMembers(Guid groupId, [FromBody] UpdateGroupMembersRequest body, CancellationToken ct)
    {
        try
        {
            if (body?.UserIds is null || body.UserIds.Count == 0)
                return BadRequest(new { error = "UserIds are required." });

            var verifyMembers = await RequireUsersVerifiedAsync(body.UserIds, "В группу можно добавлять только пользователей с подтверждённой почтой.", ct);
            if (verifyMembers is not null) return verifyMembers;

            var beforeMemberIds = await db.GroupChatMembers.AsNoTracking()
                .Where(m => m.GroupChatId == groupId)
                .Select(m => m.UserId)
                .ToListAsync(ct);
            var beforeSet = beforeMemberIds.ToHashSet();

            await groupChats.AddMembersAsync(groupId, MeId, body.UserIds, ct);
            var members = await groupChats.GetMemberDtosAsync(groupId, ct);
            var addedIds = members
                .Select(m => m.UserId)
                .Where(id => !beforeSet.Contains(id))
                .Distinct()
                .ToList();

            await BroadcastGroupMembersChanged(groupId, ct);

            var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
            if (summary is not null)
                await BroadcastGroupUpdated(summary, ct);

            var addedNames = await GetPublicUserNamesAsync(addedIds, ct);
            foreach (var addedId in addedIds)
            {
                var name = addedNames.TryGetValue(addedId, out var publicName) ? publicName : "Пользователь";
                await CreateAndBroadcastGroupSystemMessageAsync(
                    groupId,
                    MeId,
                    GroupChatService.SystemUserAddedKey,
                    $"Пользователь {name} добавлен в группу.",
                    ct);
            }

            if (addedIds.Count > 0)
            {
                await CreateAndBroadcastGroupSystemMessageAsync(
                    groupId,
                    MeId,
                    GroupChatService.SystemSecurityKeysUpdatedKey,
                    "Ключи безопасности группы обновлены.",
                    ct);
            }

            return Ok(members);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AddGroupMembers failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/{groupId:guid}/history-key-packages")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<GroupHistoryKeyPackageImportResult>> UploadGroupHistoryKeyPackages(Guid groupId, [FromBody] GroupHistoryKeyPackagesUploadRequest body, CancellationToken ct)
    {
        try
        {
            if (body?.Packages is null || body.Packages.Count == 0)
                return Ok(new GroupHistoryKeyPackageImportResult(0, 0));

            var chatEntity = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct);
            if (chatEntity is null) return NotFound(new { error = "Group chat not found." });

            var actor = await db.GroupChatMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == MeId, ct);
            if (actor is null) return Forbid();
            if (chatEntity.OwnerId != MeId && actor.Role != GroupChatRole.Admin)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Only the owner or admin can share group history." });
            if (chatEntity.HistoryPolicy != 1)
                return BadRequest(new { error = "Old history is disabled for new members in this group." });

            var memberIds = await db.GroupChatMembers.AsNoTracking()
                .Where(m => m.GroupChatId == groupId)
                .Select(m => m.UserId)
                .ToListAsync(ct);
            var memberSet = memberIds.ToHashSet();

            var accepted = 0;
            var skipped = 0;
            var now = DateTime.UtcNow;

            foreach (var package in body.Packages.Take(1000))
            {
                var targetDeviceId = CleanHistoryPackageText(package.TargetDeviceId, 128);
                var senderDeviceId = CleanHistoryPackageText(package.SenderDeviceId, 128);
                var senderKeyId = CleanHistoryPackageText(package.SenderKeyId, 128);
                var providerDeviceId = CleanHistoryPackageText(package.ProviderDeviceId, 128);
                var algorithm = CleanHistoryPackageText(package.Algorithm, 96) ?? "JZ-GROUP-HISTORY-KEY-P256-AESGCM-v1";
                var providerPublic = CleanHistoryPackageText(package.ProviderPublicKeyBase64, 8192);
                var targetPublic = CleanHistoryPackageText(package.TargetPublicKeyBase64, 8192);
                var nonce = CleanHistoryPackageText(package.NonceBase64, 256);
                var cipher = CleanHistoryPackageText(package.CiphertextBase64, 8192);
                var tag = CleanHistoryPackageText(package.TagBase64, 256);

                if (package.TargetUserId == Guid.Empty || package.SenderUserId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(targetDeviceId) || string.IsNullOrWhiteSpace(senderDeviceId) ||
                    string.IsNullOrWhiteSpace(senderKeyId) || string.IsNullOrWhiteSpace(providerDeviceId) ||
                    string.IsNullOrWhiteSpace(providerPublic) || string.IsNullOrWhiteSpace(targetPublic) ||
                    string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(cipher) || string.IsNullOrWhiteSpace(tag))
                {
                    skipped++;
                    continue;
                }

                if (!memberSet.Contains(package.TargetUserId))
                {
                    skipped++;
                    continue;
                }

                var epoch = Math.Clamp(package.SecurityEpoch, 1, Math.Max(1, chatEntity.SecurityEpoch));
                var exists = await db.GroupHistoryKeyPackages.AnyAsync(x =>
                    x.GroupChatId == groupId &&
                    x.SecurityEpoch == epoch &&
                    x.TargetUserId == package.TargetUserId &&
                    x.TargetDeviceId == targetDeviceId &&
                    x.SenderUserId == package.SenderUserId &&
                    x.SenderDeviceId == senderDeviceId &&
                    x.SenderKeyId == senderKeyId, ct);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                db.GroupHistoryKeyPackages.Add(new GroupHistoryKeyPackage
                {
                    GroupChatId = groupId,
                    SenderUserId = package.SenderUserId,
                    SenderDeviceId = senderDeviceId,
                    SenderKeyId = senderKeyId,
                    SecurityEpoch = epoch,
                    ProviderUserId = MeId,
                    ProviderDeviceId = providerDeviceId,
                    TargetUserId = package.TargetUserId,
                    TargetDeviceId = targetDeviceId,
                    ProviderPublicKeyBase64 = providerPublic,
                    TargetPublicKeyBase64 = targetPublic,
                    NonceBase64 = nonce,
                    CiphertextBase64 = cipher,
                    TagBase64 = tag,
                    Algorithm = algorithm,
                    CreatedAt = now
                });
                accepted++;
            }

            if (accepted > 0)
            {
                await db.SaveChangesAsync(ct);
                await CreateAndBroadcastGroupSystemMessageAsync(
                    groupId,
                    MeId,
                    GroupChatService.SystemHistoryAvailableKey,
                    "История группы доступна новому участнику.",
                    ct);
            }

            return Ok(new GroupHistoryKeyPackageImportResult(accepted, skipped));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "UploadGroupHistoryKeyPackages failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("groups/{groupId:guid}/history-key-packages")]
    public async Task<ActionResult<IReadOnlyList<GroupHistoryKeyPackageDto>>> GetGroupHistoryKeyPackages(Guid groupId, [FromQuery] string? deviceId, CancellationToken ct)
    {
        try
        {
            var normalizedDeviceId = CleanHistoryPackageText(deviceId, 128);
            if (string.IsNullOrWhiteSpace(normalizedDeviceId))
                return BadRequest(new { error = "DeviceId is required." });

            var isMember = await db.GroupChatMembers.AsNoTracking()
                .AnyAsync(m => m.GroupChatId == groupId && m.UserId == MeId, ct);
            if (!isMember) return Forbid();

            var rows = await db.GroupHistoryKeyPackages
                .Where(x => x.GroupChatId == groupId && x.TargetUserId == MeId && x.TargetDeviceId == normalizedDeviceId)
                .OrderBy(x => x.SecurityEpoch)
                .ThenBy(x => x.CreatedAt)
                .Take(1000)
                .ToListAsync(ct);

            if (rows.Count > 0)
            {
                var now = DateTime.UtcNow;
                foreach (var row in rows.Where(x => x.DeliveredAt == null))
                    row.DeliveredAt = now;
                await db.SaveChangesAsync(ct);
            }

            return Ok(rows.Select(x => new GroupHistoryKeyPackageDto(
                x.Id,
                x.GroupChatId,
                x.SenderUserId,
                x.SenderDeviceId,
                x.SenderKeyId,
                Math.Max(1, x.SecurityEpoch),
                x.ProviderUserId,
                x.ProviderDeviceId,
                x.TargetUserId,
                x.TargetDeviceId,
                x.ProviderPublicKeyBase64,
                x.TargetPublicKeyBase64,
                x.NonceBase64,
                x.CiphertextBase64,
                x.TagBase64,
                x.Algorithm,
                x.CreatedAt)).ToList());
        }
        catch (Exception ex)
        {
            log.LogError(ex, "GetGroupHistoryKeyPackages failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    private static string? CleanHistoryPackageText(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized is not null && normalized.Length > maxLength)
            normalized = normalized[..maxLength];
        return normalized;
    }

    [HttpDelete("groups/{groupId:guid}/members/{userId:guid}")]
    public async Task<ActionResult<IReadOnlyList<GroupChatMemberDto>>> RemoveGroupMember(Guid groupId, Guid userId, CancellationToken ct)
    {
        try
        {
            await groupChats.RemoveMemberAsync(groupId, MeId, userId, ct);
            var members = await groupChats.GetMemberDtosAsync(groupId, ct);
            await BroadcastGroupMembersChanged(groupId, ct);

            var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
            if (summary is not null)
                await BroadcastGroupUpdated(summary, ct);

            await CreateAndBroadcastGroupSystemMessageAsync(
                groupId,
                MeId,
                GroupChatService.SystemSecurityKeysUpdatedKey,
                "Ключи безопасности группы обновлены.",
                ct);

            return Ok(members);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "RemoveGroupMember failed: me={MeId}, group={GroupId}, user={UserId}", MeId, groupId, userId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/{groupId:guid}/members/{userId:guid}/role")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<IReadOnlyList<GroupChatMemberDto>>> UpdateGroupMemberRole(Guid groupId, Guid userId, [FromBody] UpdateGroupMemberRoleRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null || !GroupChatRoleInfo.TryParse(body.Role, out var role))
                return BadRequest(new { error = "Valid role is required." });

            await groupChats.UpdateMemberRoleAsync(groupId, MeId, userId, role, ct);
            var members = await groupChats.GetMemberDtosAsync(groupId, ct);
            await BroadcastGroupMembersChanged(groupId, ct);

            var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
            if (summary is not null)
                await BroadcastGroupUpdated(summary, ct);

            return Ok(members);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "UpdateGroupMemberRole failed: me={MeId}, group={GroupId}, user={UserId}", MeId, groupId, userId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/{groupId:guid}/leave")]
    public async Task<IActionResult> LeaveGroup(Guid groupId, CancellationToken ct)
    {
        try
        {
            var ownerId = await db.GroupChats.AsNoTracking()
                .Where(g => g.Id == groupId)
                .Select(g => g.OwnerId)
                .FirstOrDefaultAsync(ct);

            await groupChats.LeaveAsync(groupId, MeId, ct);

            if (ownerId != Guid.Empty)
            {
                await CreateAndBroadcastGroupSystemMessageAsync(
                    groupId,
                    ownerId,
                    GroupChatService.SystemSecurityKeysUpdatedKey,
                    "Ключи безопасности группы обновлены.",
                    ct);
            }

            var memberIds = await GetGroupMemberIdsAsync(groupId, ct);
            foreach (var memberId in memberIds)
            {
                var summary = await groupChats.GetSummaryAsync(groupId, memberId, ct);
                if (summary is not null)
                    await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatUpdated", new GroupChatUpdatedDto(summary), ct);
            }

            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "LeaveGroup failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("groups/{groupId:guid}/avatar/url")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<GroupChatSummaryDto>> SetGroupAvatarUrl(Guid groupId, [FromBody] SetGroupAvatarUrlRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });

            await groupChats.UpdateAvatarUrlAsync(groupId, MeId, body.AvatarUrl, ct);
            var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
            if (summary is null) return NotFound(new { error = "Group chat not found." });

            await BroadcastGroupUpdated(summary, ct);
            return Ok(summary);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SetGroupAvatarUrl failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [RequestSizeLimit(5 * 1024 * 1024)]
    [HttpPost("groups/{groupId:guid}/avatar/upload")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<object>> UploadGroupAvatar(Guid groupId, IFormFile file, CancellationToken ct)
    {
        try
        {
            var group = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct);
            if (group is null) return NotFound(new { error = "Group chat not found." });

            var actor = await db.GroupChatMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == MeId, ct);
            if (actor is null) return Forbid();
            if (group.OwnerId != MeId && actor.Role != GroupChatRole.Admin) return Forbid();

            if (file == null || file.Length == 0)
                return BadRequest(new { error = "Файл не найден." });

            var allowed = new[] { "image/png", "image/jpeg", "image/webp" };
            if (!allowed.Contains(file.ContentType ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                return BadRequest(new { error = "Поддерживаются только PNG/JPEG/WEBP." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
                return BadRequest(new { error = "Неверное расширение файла." });

            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            if (bytes.Length == 0)
                return BadRequest(new { error = "Пустой файл." });

            db.GroupAvatars.Add(new GroupAvatar
            {
                GroupChatId = groupId,
                Data = bytes,
                ContentType = file.ContentType ?? "image/png",
                CreatedAt = DateTime.UtcNow
            });

            var version = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            group.AvatarUrl = $"/avatars/groups/{groupId}?v={version}";
            group.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
            if (summary is not null)
                await BroadcastGroupUpdated(summary, ct);

            return Ok(new { url = group.AvatarUrl });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "UploadGroupAvatar failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [AllowAnonymous]
    [HttpGet("/avatars/groups/{id:guid}")]
    public async Task<IActionResult> GetGroupAvatar(Guid id, CancellationToken ct)
    {
        var avatar = await db.GroupAvatars.AsNoTracking()
            .Where(a => a.GroupChatId == id)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (avatar is null || avatar.Data.Length == 0)
        {
            var path = GetGroupDefaultAvatarPath();
            if (System.IO.File.Exists(path))
                return PhysicalFile(path, "image/png");
            return NotFound();
        }

        var etag = $"W/\"{avatar.Data.Length}-{avatar.CreatedAt.ToUniversalTime():yyyyMMddHHmmss}\"";
        var ifNone = Request.Headers["If-None-Match"].ToString();
        if (!string.IsNullOrEmpty(ifNone) && string.Equals(ifNone, etag, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status304NotModified);

        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public,max-age=3600";
        return File(avatar.Data, avatar.ContentType ?? "image/png");
    }

    [HttpGet("groups/{groupId:guid}/history")]
    public async Task<ActionResult<IEnumerable<MessageDto>>> GroupHistory(
        Guid groupId,
        int skip = 0,
        int take = 50,
        DateTime? before = null,
        Guid? beforeId = null,
        DateTime? after = null,
        Guid? afterId = null,
        CancellationToken ct = default)
    {
        try
        {
            var access = await (
                from member in db.GroupChatMembers.AsNoTracking()
                join groupChat in db.GroupChats.AsNoTracking() on member.GroupChatId equals groupChat.Id
                where member.GroupChatId == groupId && member.UserId == MeId
                select new { Member = member, Group = groupChat }
            ).FirstOrDefaultAsync(ct);

            if (access is null)
                return NotFound(new { error = "Group chat not found." });

            var q = db.GroupMessages.AsNoTracking().Where(m => m.GroupChatId == groupId);
            if (access.Group.HistoryPolicy != 1)
            {
                var joinedAt = DirectChatService.EnsureUtc(access.Member.JoinedAt);
                q = q.Where(m => m.SentAt >= joinedAt);
            }

            if (before.HasValue)
            {
                var bt = DirectChatService.EnsureUtc(before.Value);
                if (beforeId.HasValue)
                {
                    var bid = beforeId.Value;
                    q = q.Where(m => m.SentAt < bt || (m.SentAt == bt && m.Id.CompareTo(bid) < 0));
                }
                else
                {
                    q = q.Where(m => m.SentAt < bt);
                }
            }

            if (after.HasValue)
            {
                var at = DirectChatService.EnsureUtc(after.Value);
                if (afterId.HasValue)
                {
                    var aid = afterId.Value;
                    q = q.Where(m => m.SentAt > at || (m.SentAt == at && m.Id.CompareTo(aid) > 0));
                }
                else
                {
                    q = q.Where(m => m.SentAt > at);
                }
            }

            List<GroupMessage> rows;
            var normalizedTake = Math.Clamp(take, 1, 200);

            if (before.HasValue && !after.HasValue)
            {
                rows = await q.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).Take(normalizedTake).ToListAsync(ct);
                rows = rows.OrderBy(m => m.SentAt).ThenBy(m => m.Id).ToList();
            }
            else
            {
                rows = await q.OrderBy(m => m.SentAt).ThenBy(m => m.Id).Skip(Math.Max(0, skip)).Take(normalizedTake).ToListAsync(ct);
            }

            return Ok(await groupChats.BuildMessageDtosAsync(rows, ct));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "GroupHistory failed: me={MeId}, group={GroupId}", MeId, groupId);
            return Ok(Array.Empty<MessageDto>());
        }
    }

    [HttpPost("groups/{groupId:guid}/messages/send")]
    [EnableRateLimiting("chat-write")]
    [RequireVerifiedEmail]
    [RequireRiskCaptcha("group-message", 12, 30)]
    public async Task<ActionResult<MessageDto>> SendGroupMessage(Guid groupId, [FromBody] SendMessageRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });

            var created = await groupChats.CreateMessageAsync(MeId, groupId, body.Text, body.FileIds, DirectMessageKind.User, null, null, ct);
            var dto = await groupChats.GetMessageDtoAsync(groupId, created.message.Id, ct);
            if (dto is null) return Problem("Failed to build message dto.", statusCode: 500);

            await BroadcastGroupCreated(groupId, dto, ct);
            return Ok(dto);
        }
        catch (DbUpdateException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            log.LogError(ex, "SendGroupMessage db update failed: me={MeId}, group={GroupId}, detail={Detail}", MeId, groupId, detail);
            return BadRequest(new { error = detail });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SendGroupMessage failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/{groupId:guid}/mark-read")]
    public async Task<IActionResult> MarkGroupRead(Guid groupId, [FromBody] MarkReadRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });
            if (body.LastReadMessageId == Guid.Empty) return BadRequest(new { error = "LastReadMessageId is required." });

            var member = await db.GroupChatMembers.FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == MeId, ct);
            if (member is null) return NotFound(new { error = "Group chat not found." });

            var cursorMessage = await VisibleGroupMessagesForMe(groupId)
                .Where(m => m.Id == body.LastReadMessageId)
                .Select(m => new { m.Id, m.SentAt })
                .FirstOrDefaultAsync(ct);

            if (cursorMessage is null)
                return BadRequest(new { error = "Cursor message was not found in this group." });

            var at = DirectChatService.EnsureUtc(cursorMessage.SentAt);
            var mid = cursorMessage.Id;
            var curAt = DirectChatService.EnsureUtc(member.LastReadAt);
            var curId = member.LastReadMessageId;

            if (at > curAt || (at == curAt && mid.CompareTo(curId) > 0))
            {
                member.LastReadAt = at;
                member.LastReadMessageId = mid;
                await db.SaveChangesAsync(ct);
            }

            var unread = await groupChats.GetUnreadForUserAsync(groupId, MeId, ct);
            await hub.Clients.User(MeId.ToString()).SendAsync("GroupChatUnreadChanged", new GroupChatUnreadChangedDto(groupId, unread.count, unread.firstId, unread.firstAt), ct);

            var readDto = new GroupChatMessageReadDto(groupId, MeId, mid, DateTime.UtcNow);
            var memberIds = await GetGroupMemberIdsAsync(groupId, ct);
            foreach (var memberId in memberIds)
                await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatMessageRead", readDto, ct);

            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "MarkGroupRead failed: me={MeId}, group={GroupId}", MeId, groupId);
            return Ok(new { ok = true });
        }
    }

    [HttpPost("groups/{groupId:guid}/system")]
    public async Task<ActionResult<MessageDto>> SendGroupSystemMessage(Guid groupId, [FromBody] SendSystemMessageRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.SystemKey))
                return BadRequest(new { error = "SystemKey is required." });
            if (!IsKnownGroupSystemKey(body.SystemKey))
                return BadRequest(new { error = "Unknown system message key." });
            if (!await CanCreateGroupSystemMessageAsync(groupId, MeId, ct))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Only the owner or admin can create system messages." });

            var text = CleanSystemMessageText(body.Text);
            if (string.IsNullOrWhiteSpace(text))
                text = body.SystemKey switch
                {
                    GroupChatService.SystemUserAddedKey => "Пользователь добавлен в группу.",
                    GroupChatService.SystemHistoryAvailableKey => "История группы доступна новому участнику.",
                    GroupChatService.SystemSecurityKeysUpdatedKey => "Ключи безопасности группы обновлены.",
                    GroupChatService.SystemGroupCallStartedKey => "Пользователь открыл групповой канал.",
                    GroupChatService.SystemGroupCallEndedKey => "Групповой звонок закончился.",
                    _ => "Системное сообщение."
                };

            var dto = await CreateAndBroadcastGroupSystemMessageAsync(groupId, MeId, body.SystemKey, text, ct);
            if (dto is null) return Problem("Failed to build message dto.", statusCode: 500);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SendGroupSystemMessage failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/messages/{messageId:guid}/edit")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<MessageDto>> EditGroupMessage(Guid messageId, [FromBody] EditMessageRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });
            var newText = (body.Text ?? string.Empty).Trim();

            var msg = await db.GroupMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
            if (msg is null) return NotFound(new { error = "Message not found." });
            if (msg.SenderId != MeId) return Forbid();
            if (msg.DeletedAt.HasValue) return BadRequest(new { error = "Deleted message cannot be edited." });
            if (msg.Kind != DirectMessageKind.User) return BadRequest(new { error = "Only user messages can be edited." });
            if (!await groupChats.IsMemberAsync(msg.GroupChatId, MeId, ct)) return Forbid();

            var hasAttachments = await db.GroupMessageAttachments.AsNoTracking().AnyAsync(a => a.MessageId == msg.Id, ct);
            if (string.IsNullOrWhiteSpace(newText) && !hasAttachments)
                return BadRequest(new { error = "Text is required when message has no attachments." });

            if (string.Equals(msg.Text, newText, StringComparison.Ordinal))
            {
                var same = await groupChats.GetMessageDtoAsync(msg.GroupChatId, msg.Id, ct);
                return Ok(same);
            }

            msg.Text = newText;
            msg.EditedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var dto = await groupChats.GetMessageDtoAsync(msg.GroupChatId, msg.Id, ct);
            if (dto is null) return NotFound();

            await BroadcastGroupUpdatedMessage(msg.GroupChatId, dto, ct);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "EditGroupMessage failed: me={MeId}, message={MessageId}", MeId, messageId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/messages/{messageId:guid}/delete")]
    [RequireVerifiedEmail]
    public async Task<IActionResult> DeleteGroupMessage(Guid messageId, CancellationToken ct)
    {
        try
        {
            var msg = await db.GroupMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
            if (msg is null) return NotFound(new { error = "Message not found." });
            if (msg.SenderId != MeId) return Forbid();
            if (!await groupChats.IsMemberAsync(msg.GroupChatId, MeId, ct)) return Forbid();
            if (msg.DeletedAt.HasValue) return Ok(new { ok = true });

            var attachedFileIds = await db.GroupMessageAttachments
                .AsNoTracking()
                .Where(a => a.MessageId == msg.Id)
                .Select(a => a.FileId)
                .ToListAsync(ct);

            msg.DeletedAt = DateTime.UtcNow;
            msg.DeletedById = MeId;
            msg.EditedAt = null;
            msg.Text = string.Empty;
            msg.SystemKey = null;
            await db.SaveChangesAsync(ct);
            await fileCleanup.DeleteFilesForMessageAsync(attachedFileIds, ct);

            await BroadcastGroupDeleted(msg.GroupChatId, msg.Id, msg.DeletedAt.Value, MeId, ct);
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DeleteGroupMessage failed: me={MeId}, message={MessageId}", MeId, messageId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/{groupId:guid}/forward")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<IEnumerable<MessageDto>>> ForwardGroupMessages(Guid groupId, [FromBody] ForwardMessagesRequest body, CancellationToken ct)
    {
        try
        {
            if (body?.MessageIds is null || body.MessageIds.Count == 0)
                return BadRequest(new { error = "MessageIds are required." });
            if (!await groupChats.IsMemberAsync(groupId, MeId, ct)) return NotFound(new { error = "Group chat not found." });

            var ids = body.MessageIds.Where(x => x != Guid.Empty).Distinct().Take(20).ToList();
            var sources = await VisibleGroupMessagesForMe(groupId)
                .Where(m => ids.Contains(m.Id) && m.DeletedAt == null)
                .OrderBy(m => m.SentAt)
                .ThenBy(m => m.Id)
                .ToListAsync(ct);

            if (sources.Count == 0)
                return Ok(Array.Empty<MessageDto>());

            var createdDtos = new List<MessageDto>();
            foreach (var source in sources)
            {
                var forwarded = await groupChats.ForwardMessageAsync(MeId, groupId, source, body.IncludeAttachments, ct);
                var dto = await groupChats.GetMessageDtoAsync(groupId, forwarded.message.Id, ct);
                if (dto is not null)
                {
                    createdDtos.Add(dto);
                    await BroadcastGroupCreated(groupId, dto, ct);
                }
            }

            return Ok(createdDtos);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ForwardGroupMessages failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("forward/direct/{friendId:guid}")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<IEnumerable<MessageDto>>> ForwardMessagesToDirect(Guid friendId, [FromBody] CrossChatForwardRequest body, CancellationToken ct)
    {
        try
        {
            if (body?.MessageIds is null || body.MessageIds.Count == 0)
                return BadRequest(new { error = "MessageIds are required." });
            if (!await chat.AreFriends(MeId, friendId, ct)) return Forbid();
            var peerVerified = await IsEmailConfirmedAsync(friendId, ct);
            if (!peerVerified) return StatusCode(StatusCodes.Status403Forbidden, new { code = "email_not_verified", message = "Собеседник ещё не подтвердил почту. Пересылка пока недоступна." });

            var ids = body.MessageIds.Where(x => x != Guid.Empty).Distinct().Take(20).ToList();
            var source = (body.Source ?? string.Empty).Trim().ToLowerInvariant();
            var createdDtos = new List<MessageDto>();

            if (source == "group")
            {
                var sourcesQuery = VisibleGroupMessagesForMe(body.SourceChatId)
                    .Where(m => ids.Contains(m.Id) && m.DeletedAt == null);

                var sources = await sourcesQuery.OrderBy(m => m.SentAt).ThenBy(m => m.Id).ToListAsync(ct);
                foreach (var sourceMessage in sources)
                {
                    var forwarded = await chat.ForwardMessageAsync(MeId, friendId, sourceMessage, body.IncludeAttachments, ct);
                    var dto = await chat.GetMessageDtoAsync(forwarded.dialog.Id, forwarded.message.Id, ct);
                    if (dto is not null)
                    {
                        createdDtos.Add(dto);
                        await BroadcastCreated(friendId, dto, ct);
                    }
                }
            }
            else if (source == "direct")
            {
                var sources = await (
                    from m in db.DirectMessages.AsNoTracking()
                    join d in db.DirectDialogs.AsNoTracking() on m.DialogId equals d.Id
                    where ids.Contains(m.Id) && m.DeletedAt == null && (d.User1Id == MeId || d.User2Id == MeId)
                    orderby m.SentAt, m.Id
                    select m
                ).ToListAsync(ct);

                foreach (var sourceMessage in sources)
                {
                    var forwarded = await chat.ForwardMessageAsync(MeId, friendId, sourceMessage, body.IncludeAttachments, ct);
                    var dto = await chat.GetMessageDtoAsync(forwarded.dialog.Id, forwarded.message.Id, ct);
                    if (dto is not null)
                    {
                        createdDtos.Add(dto);
                        await BroadcastCreated(friendId, dto, ct);
                    }
                }
            }
            else
            {
                return BadRequest(new { error = "Source must be 'direct' or 'group'." });
            }

            return Ok(createdDtos);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ForwardMessagesToDirect failed: me={MeId}, friend={FriendId}", MeId, friendId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("forward/group/{groupId:guid}")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<IEnumerable<MessageDto>>> ForwardMessagesToGroup(Guid groupId, [FromBody] CrossChatForwardRequest body, CancellationToken ct)
    {
        try
        {
            if (body?.MessageIds is null || body.MessageIds.Count == 0)
                return BadRequest(new { error = "MessageIds are required." });
            if (!await groupChats.IsMemberAsync(groupId, MeId, ct)) return NotFound(new { error = "Group chat not found." });

            var ids = body.MessageIds.Where(x => x != Guid.Empty).Distinct().Take(20).ToList();
            var source = (body.Source ?? string.Empty).Trim().ToLowerInvariant();
            var createdDtos = new List<MessageDto>();

            if (source == "group")
            {
                var sourcesQuery = VisibleGroupMessagesForMe(body.SourceChatId)
                    .Where(m => ids.Contains(m.Id) && m.DeletedAt == null);

                var sources = await sourcesQuery.OrderBy(m => m.SentAt).ThenBy(m => m.Id).ToListAsync(ct);
                foreach (var sourceMessage in sources)
                {
                    var forwarded = await groupChats.ForwardMessageAsync(MeId, groupId, sourceMessage, body.IncludeAttachments, ct);
                    var dto = await groupChats.GetMessageDtoAsync(groupId, forwarded.message.Id, ct);
                    if (dto is not null)
                    {
                        createdDtos.Add(dto);
                        await BroadcastGroupCreated(groupId, dto, ct);
                    }
                }
            }
            else if (source == "direct")
            {
                var sources = await (
                    from m in db.DirectMessages.AsNoTracking()
                    join d in db.DirectDialogs.AsNoTracking() on m.DialogId equals d.Id
                    where ids.Contains(m.Id) && m.DeletedAt == null && (d.User1Id == MeId || d.User2Id == MeId)
                    orderby m.SentAt, m.Id
                    select m
                ).ToListAsync(ct);

                foreach (var sourceMessage in sources)
                {
                    var forwarded = await groupChats.ForwardMessageAsync(MeId, groupId, sourceMessage, body.IncludeAttachments, ct);
                    var dto = await groupChats.GetMessageDtoAsync(groupId, forwarded.message.Id, ct);
                    if (dto is not null)
                    {
                        createdDtos.Add(dto);
                        await BroadcastGroupCreated(groupId, dto, ct);
                    }
                }
            }
            else
            {
                return BadRequest(new { error = "Source must be 'direct' or 'group'." });
            }

            return Ok(createdDtos);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ForwardMessagesToGroup failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }


    private static readonly Regex UrlRegex = new(
        @"(?i)\bhttps?://[^\s<>""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(120));

    private static bool IsAudioContent(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    private static bool IsImageContent(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideoContent(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAttachmentKind(string? type)
    {
        var t = (type ?? string.Empty).Trim().ToLowerInvariant();
        return t switch
        {
            "image" or "images" or "photo" or "photos" or "фото" => "photo",
            "video" or "videos" or "видео" => "video",
            "audio" or "music" or "музыка" => "music",
            "file" or "files" or "файлы" => "files",
            "link" or "links" or "ссылки" => "links",
            _ => "photo"
        };
    }

    private static string CleanLink(string value)
    {
        var v = (value ?? string.Empty).Trim();
        while (v.Length > 0 && ".,;:!?)]}".Contains(v[^1]))
            v = v[..^1];
        return v;
    }

    private static List<string> ExtractLinks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("http", StringComparison.OrdinalIgnoreCase))
            return [];

        try
        {
            return UrlRegex.Matches(text)
                .Select(m => CleanLink(m.Value))
                .Where(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string BuildAttachmentFileKind(ChatFile f)
    {
        if (f.Kind == StoredFileKind.Photo || IsImageContent(f.ContentType)) return "photo";
        if (f.Kind == StoredFileKind.Video || IsVideoContent(f.ContentType)) return "video";
        if (f.Kind == StoredFileKind.Music || IsAudioContent(f.ContentType)) return "music";
        return "files";
    }

    private static bool FileMatchesAttachmentType(ChatFile f, string normalizedType)
    {
        var contentType = f.ContentType ?? string.Empty;
        return normalizedType switch
        {
            "photo" => f.Kind == StoredFileKind.Photo || IsImageContent(contentType),
            "video" => f.Kind == StoredFileKind.Video || IsVideoContent(contentType),
            "music" => f.Kind == StoredFileKind.Music || IsAudioContent(contentType),
            "files" => f.Kind != StoredFileKind.Avatar &&
                       f.Kind != StoredFileKind.Photo &&
                       f.Kind != StoredFileKind.Video &&
                       f.Kind != StoredFileKind.Music &&
                       !IsImageContent(contentType) &&
                       !IsVideoContent(contentType) &&
                       !IsAudioContent(contentType),
            _ => false
        };
    }

    private ChatAttachmentBrowserItemDto ToAttachmentBrowserDto(ChatFile file, Guid messageId, DateTime sentAt, Guid senderId, string? senderName)
    {
        var kind = BuildAttachmentFileKind(file);
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var scanStatus = file.ScanStatus.ToString();
        var isSafe = file.ScanStatus == FileScanStatus.Clean;
        return new ChatAttachmentBrowserItemDto(
            file.Id,
            messageId,
            sentAt,
            senderId,
            string.IsNullOrWhiteSpace(senderName) ? "Пользователь" : senderName!,
            kind,
            string.IsNullOrWhiteSpace(file.OriginalFileName) ? "file" : file.OriginalFileName,
            $"/api/files/{file.Id}/raw",
            contentType,
            file.SizeBytes,
            kind == "photo",
            kind == "video",
            kind == "music",
            scanStatus,
            file.ScanStatus != FileScanStatus.NotScanned,
            isSafe,
            isSafe ? null : (file.RiskNote ?? "Файл ещё не прошёл антивирусную проверку."),
            null);
    }

    [HttpGet("attachments/direct/{friendId:guid}")]
    public async Task<ActionResult<IEnumerable<ChatAttachmentBrowserItemDto>>> DirectAttachments(Guid friendId, string? type = "photo", int take = 250, CancellationToken ct = default)
    {
        try
        {
            var normalizedType = NormalizeAttachmentKind(type);
            if (!await chat.AreFriends(MeId, friendId, ct)) return Forbid();
            var dlg = await chat.GetOrCreateDialogAsync(MeId, friendId, ct);
            var safeTake = Math.Clamp(take, 1, 500);

            if (normalizedType == "links")
            {
                var messages = await (
                    from m in db.DirectMessages.AsNoTracking()
                    join u in db.Users.AsNoTracking() on m.SenderId equals u.Id
                    where m.DialogId == dlg.Id && m.DeletedAt == null && m.Text != null && m.Text != string.Empty
                    orderby m.SentAt descending, m.Id descending
                    select new { Message = m, SenderName = u.DisplayName ?? u.UserName }
                ).Take(1000).ToListAsync(ct);

                var links = new List<ChatAttachmentBrowserItemDto>();
                foreach (var row in messages)
                {
                    foreach (var link in ExtractLinks(row.Message.Text))
                    {
                        links.Add(new ChatAttachmentBrowserItemDto(
                            Guid.NewGuid(),
                            row.Message.Id,
                            row.Message.SentAt,
                            row.Message.SenderId,
                            string.IsNullOrWhiteSpace(row.SenderName) ? "Пользователь" : row.SenderName,
                            "links",
                            link,
                            link,
                            "text/uri-list",
                            0,
                            false,
                            false,
                            false,
                            "Clean",
                            true,
                            true,
                            null,
                            row.Message.Text));
                        if (links.Count >= safeTake) return Ok(links);
                    }
                }

                return Ok(links);
            }

            var rows = await (
                from a in db.DirectMessageAttachments.AsNoTracking()
                join m in db.DirectMessages.AsNoTracking() on a.MessageId equals m.Id
                join f in db.ChatFiles.AsNoTracking() on a.FileId equals f.Id
                join u in db.Users.AsNoTracking() on m.SenderId equals u.Id
                where m.DialogId == dlg.Id && m.DeletedAt == null && f.DeletedAt == null && f.BlockedAt == null
                orderby m.SentAt descending, m.Id descending, a.CreatedAt descending
                select new { File = f, Message = m, SenderName = u.DisplayName ?? u.UserName }
            ).Take(1000).ToListAsync(ct);

            var result = rows
                .Where(x => FileMatchesAttachmentType(x.File, normalizedType))
                .Select(x => ToAttachmentBrowserDto(x.File, x.Message.Id, x.Message.SentAt, x.Message.SenderId, x.SenderName))
                .Take(safeTake)
                .ToList();

            return Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DirectAttachments failed: me={MeId}, friend={FriendId}", MeId, friendId);
            return Ok(Array.Empty<ChatAttachmentBrowserItemDto>());
        }
    }

    [HttpGet("attachments/groups/{groupId:guid}")]
    public async Task<ActionResult<IEnumerable<ChatAttachmentBrowserItemDto>>> GroupAttachments(Guid groupId, string? type = "photo", int take = 250, CancellationToken ct = default)
    {
        try
        {
            var normalizedType = NormalizeAttachmentKind(type);
            if (!await groupChats.IsMemberAsync(groupId, MeId, ct)) return NotFound(new { error = "Group chat not found." });
            var safeTake = Math.Clamp(take, 1, 500);

            if (normalizedType == "links")
            {
                var visibleMessages = VisibleGroupMessagesForMe(groupId);
                var messages = await (
                    from m in visibleMessages
                    join u in db.Users.AsNoTracking() on m.SenderId equals u.Id
                    where m.DeletedAt == null && m.Text != null && m.Text != string.Empty
                    orderby m.SentAt descending, m.Id descending
                    select new { Message = m, SenderName = u.DisplayName ?? u.UserName }
                ).Take(1000).ToListAsync(ct);

                var links = new List<ChatAttachmentBrowserItemDto>();
                foreach (var row in messages)
                {
                    foreach (var link in ExtractLinks(row.Message.Text))
                    {
                        links.Add(new ChatAttachmentBrowserItemDto(
                            Guid.NewGuid(),
                            row.Message.Id,
                            row.Message.SentAt,
                            row.Message.SenderId,
                            string.IsNullOrWhiteSpace(row.SenderName) ? "Пользователь" : row.SenderName,
                            "links",
                            link,
                            link,
                            "text/uri-list",
                            0,
                            false,
                            false,
                            false,
                            "Clean",
                            true,
                            true,
                            null,
                            row.Message.Text));
                        if (links.Count >= safeTake) return Ok(links);
                    }
                }

                return Ok(links);
            }

            var visibleAttachmentMessages = VisibleGroupMessagesForMe(groupId);
            var rows = await (
                from a in db.GroupMessageAttachments.AsNoTracking()
                join m in visibleAttachmentMessages on a.MessageId equals m.Id
                join f in db.ChatFiles.AsNoTracking() on a.FileId equals f.Id
                join u in db.Users.AsNoTracking() on m.SenderId equals u.Id
                where m.DeletedAt == null && f.DeletedAt == null && f.BlockedAt == null
                orderby m.SentAt descending, m.Id descending, a.CreatedAt descending
                select new { File = f, Message = m, SenderName = u.DisplayName ?? u.UserName }
            ).Take(1000).ToListAsync(ct);

            var result = rows
                .Where(x => FileMatchesAttachmentType(x.File, normalizedType))
                .Select(x => ToAttachmentBrowserDto(x.File, x.Message.Id, x.Message.SentAt, x.Message.SenderId, x.SenderName))
                .Take(safeTake)
                .ToList();

            return Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "GroupAttachments failed: me={MeId}, group={GroupId}", MeId, groupId);
            return Ok(Array.Empty<ChatAttachmentBrowserItemDto>());
        }
    }

    private async Task<GroupChatDetailsDto?> BuildGroupDetailsAsync(Guid groupId, CancellationToken ct)
    {
        var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
        if (summary is null) return null;

        var members = await groupChats.GetMemberDtosAsync(groupId, ct);
        return new GroupChatDetailsDto(summary, members);
    }

    private async Task<List<Guid>> GetGroupMemberIdsAsync(Guid groupId, CancellationToken ct) =>
        await db.GroupChatMembers.AsNoTracking().Where(m => m.GroupChatId == groupId).Select(m => m.UserId).ToListAsync(ct);

    private IQueryable<GroupMessage> VisibleGroupMessagesForMe(Guid? groupId = null)
    {
        var me = MeId;
        var query =
            from message in db.GroupMessages.AsNoTracking()
            join member in db.GroupChatMembers.AsNoTracking().Where(m => m.UserId == me)
                on message.GroupChatId equals member.GroupChatId
            join groupChat in db.GroupChats.AsNoTracking()
                on message.GroupChatId equals groupChat.Id
            where groupChat.HistoryPolicy == 1 || message.SentAt >= member.JoinedAt
            select message;

        if (groupId.HasValue && groupId.Value != Guid.Empty)
            query = query.Where(message => message.GroupChatId == groupId.Value);

        return query;
    }

    private async Task BroadcastGroupCreated(Guid groupId, MessageDto dto, CancellationToken ct)
    {
        var memberIds = await GetGroupMemberIdsAsync(groupId, ct);
        foreach (var memberId in memberIds)
        {
            await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatMessageCreated", new GroupChatRealtimeMessageDto(groupId, dto), ct);
        }

        foreach (var memberId in memberIds.Where(x => x != dto.SenderId))
        {
            var unread = await groupChats.GetUnreadForUserAsync(groupId, memberId, ct);
            await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatUnreadChanged", new GroupChatUnreadChangedDto(groupId, unread.count, unread.firstId, unread.firstAt), ct);
        }
    }

    private async Task BroadcastGroupUpdatedMessage(Guid groupId, MessageDto dto, CancellationToken ct)
    {
        var memberIds = await GetGroupMemberIdsAsync(groupId, ct);
        foreach (var memberId in memberIds)
        {
            await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatMessageUpdated", new GroupChatMessageUpdatedDto(groupId, dto), ct);
        }
    }

    private async Task BroadcastGroupDeleted(Guid groupId, Guid messageId, DateTime deletedAt, Guid deletedById, CancellationToken ct)
    {
        var memberIds = await GetGroupMemberIdsAsync(groupId, ct);
        foreach (var memberId in memberIds)
        {
            await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatMessageDeleted", new GroupChatMessageDeletedDto(groupId, messageId, deletedAt, deletedById), ct);
        }
    }

    private async Task BroadcastGroupUpdated(GroupChatSummaryDto summary, CancellationToken ct)
    {
        var memberIds = await GetGroupMemberIdsAsync(summary.Id, ct);
        foreach (var memberId in memberIds)
        {
            var memberSummary = memberId == MeId ? summary : await groupChats.GetSummaryAsync(summary.Id, memberId, ct);
            if (memberSummary is not null)
                await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatUpdated", new GroupChatUpdatedDto(memberSummary), ct);
        }
    }

    private async Task BroadcastGroupMembersChanged(Guid groupId, CancellationToken ct)
    {
        var members = await groupChats.GetMemberDtosAsync(groupId, ct);
        var epoch = await db.GroupChats.AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => new { g.SecurityEpoch, g.SecurityEpochChangedAt })
            .FirstOrDefaultAsync(ct);
        var securityEpoch = Math.Max(1, epoch?.SecurityEpoch ?? 1);
        var securityEpochChangedAt = epoch?.SecurityEpochChangedAt;
        foreach (var memberId in members.Select(m => m.UserId))
        {
            await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatMembersChanged", new GroupChatMembersChangedDto(groupId, members, securityEpoch, securityEpochChangedAt), ct);
        }
    }

}
