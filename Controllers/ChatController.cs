using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ChatController(
    AppDbContext db,
    ILogger<ChatController> log,
    DirectChatService chat,
    GroupChatService groupChats,
    IHubContext<ChatHub> hub) : ControllerBase
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
                result.Add(new UnreadDialogDto(friendId, count, firstId, firstAt));
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
    public async Task<ActionResult<MessageDto>> SendMessage(Guid friendId, [FromBody] SendMessageRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });
            if (!await chat.AreFriends(MeId, friendId, ct)) return Forbid();

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
    public async Task<IActionResult> DeleteMessage(Guid messageId, CancellationToken ct)
    {
        try
        {
            var msg = await db.DirectMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
            if (msg is null) return NotFound(new { error = "Message not found." });
            if (msg.SenderId != MeId) return Forbid();
            if (msg.DeletedAt.HasValue) return Ok(new { ok = true });

            msg.DeletedAt = DateTime.UtcNow;
            msg.DeletedById = MeId;
            msg.EditedAt = null;
            msg.Text = string.Empty;
            msg.SystemKey = null;
            await db.SaveChangesAsync(ct);

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
    public async Task<ActionResult<IEnumerable<MessageDto>>> ForwardMessages(Guid friendId, [FromBody] ForwardMessagesRequest body, CancellationToken ct)
    {
        try
        {
            if (body?.MessageIds is null || body.MessageIds.Count == 0)
                return BadRequest(new { error = "MessageIds are required." });
            if (!await chat.AreFriends(MeId, friendId, ct)) return Forbid();

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
                result.Add(new GroupUnreadChatDto(groupId, unread.count, unread.firstId, unread.firstAt));
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
    public async Task<ActionResult<GroupChatDetailsDto>> CreateGroup([FromBody] CreateGroupChatRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });

            var group = await groupChats.CreateChatAsync(MeId, body.Title, body.MemberIds, ct);
            var details = await BuildGroupDetailsAsync(group.Id, ct);
            if (details is null) return Problem("Failed to build group chat dto.", statusCode: 500);

            await BroadcastGroupUpdated(details.Chat, ct);
            await BroadcastGroupMembersChanged(group.Id, ct);
            return Ok(details);
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
    public async Task<ActionResult<GroupChatSummaryDto>> UpdateGroupTitle(Guid groupId, [FromBody] UpdateGroupChatRequest body, CancellationToken ct)
    {
        try
        {
            if (body is null) return BadRequest(new { error = "Body is required." });

            await groupChats.UpdateTitleAsync(groupId, MeId, body.Title, ct);
            var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
            if (summary is null) return NotFound(new { error = "Group chat not found." });

            await BroadcastGroupUpdated(summary, ct);
            return Ok(summary);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "UpdateGroupTitle failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/{groupId:guid}/members")]
    public async Task<ActionResult<IReadOnlyList<GroupChatMemberDto>>> AddGroupMembers(Guid groupId, [FromBody] UpdateGroupMembersRequest body, CancellationToken ct)
    {
        try
        {
            if (body?.UserIds is null || body.UserIds.Count == 0)
                return BadRequest(new { error = "UserIds are required." });

            await groupChats.AddMembersAsync(groupId, MeId, body.UserIds, ct);
            var members = await groupChats.GetMemberDtosAsync(groupId, ct);
            await BroadcastGroupMembersChanged(groupId, ct);

            var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
            if (summary is not null)
                await BroadcastGroupUpdated(summary, ct);

            return Ok(members);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AddGroupMembers failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
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

            return Ok(members);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "RemoveGroupMember failed: me={MeId}, group={GroupId}, user={UserId}", MeId, groupId, userId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/{groupId:guid}/leave")]
    public async Task<IActionResult> LeaveGroup(Guid groupId, CancellationToken ct)
    {
        try
        {
            await groupChats.LeaveAsync(groupId, MeId, ct);
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
            if (!await groupChats.IsMemberAsync(groupId, MeId, ct))
                return NotFound(new { error = "Group chat not found." });

            var q = db.GroupMessages.AsNoTracking().Where(m => m.GroupChatId == groupId);

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

            var cursorMessage = await db.GroupMessages.AsNoTracking()
                .Where(m => m.GroupChatId == groupId && m.Id == body.LastReadMessageId)
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

            var created = await groupChats.CreateMessageAsync(MeId, groupId, body.Text, null, DirectMessageKind.System, body.SystemKey, null, ct);
            var dto = await groupChats.GetMessageDtoAsync(groupId, created.message.Id, ct);
            if (dto is null) return Problem("Failed to build message dto.", statusCode: 500);

            await BroadcastGroupCreated(groupId, dto, ct);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SendGroupSystemMessage failed: me={MeId}, group={GroupId}", MeId, groupId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("groups/messages/{messageId:guid}/edit")]
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
    public async Task<IActionResult> DeleteGroupMessage(Guid messageId, CancellationToken ct)
    {
        try
        {
            var msg = await db.GroupMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
            if (msg is null) return NotFound(new { error = "Message not found." });
            if (msg.SenderId != MeId) return Forbid();
            if (!await groupChats.IsMemberAsync(msg.GroupChatId, MeId, ct)) return Forbid();
            if (msg.DeletedAt.HasValue) return Ok(new { ok = true });

            msg.DeletedAt = DateTime.UtcNow;
            msg.DeletedById = MeId;
            msg.EditedAt = null;
            msg.Text = string.Empty;
            msg.SystemKey = null;
            await db.SaveChangesAsync(ct);

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
    public async Task<ActionResult<IEnumerable<MessageDto>>> ForwardGroupMessages(Guid groupId, [FromBody] ForwardMessagesRequest body, CancellationToken ct)
    {
        try
        {
            if (body?.MessageIds is null || body.MessageIds.Count == 0)
                return BadRequest(new { error = "MessageIds are required." });
            if (!await groupChats.IsMemberAsync(groupId, MeId, ct)) return NotFound(new { error = "Group chat not found." });

            var ids = body.MessageIds.Where(x => x != Guid.Empty).Distinct().Take(20).ToList();
            var sources = await db.GroupMessages.AsNoTracking()
                .Where(m => ids.Contains(m.Id) && m.DeletedAt == null)
                .Join(db.GroupChatMembers.AsNoTracking().Where(m => m.UserId == MeId), m => m.GroupChatId, gm => gm.GroupChatId, (m, gm) => m)
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

    private async Task<GroupChatDetailsDto?> BuildGroupDetailsAsync(Guid groupId, CancellationToken ct)
    {
        var summary = await groupChats.GetSummaryAsync(groupId, MeId, ct);
        if (summary is null) return null;

        var members = await groupChats.GetMemberDtosAsync(groupId, ct);
        return new GroupChatDetailsDto(summary, members);
    }

    private async Task<List<Guid>> GetGroupMemberIdsAsync(Guid groupId, CancellationToken ct) =>
        await db.GroupChatMembers.AsNoTracking().Where(m => m.GroupChatId == groupId).Select(m => m.UserId).ToListAsync(ct);

    private async Task BroadcastGroupCreated(Guid groupId, MessageDto dto, CancellationToken ct)
    {
        var memberIds = await GetGroupMemberIdsAsync(groupId, ct);
        foreach (var memberId in memberIds)
        {
            await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatMessageCreated", new GroupChatRealtimeMessageDto(groupId, dto), ct);
        }

        foreach (var memberId in memberIds.Where(x => x != MeId))
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
        foreach (var memberId in members.Select(m => m.UserId))
        {
            await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatMembersChanged", new GroupChatMembersChangedDto(groupId, members), ct);
        }
    }

}
