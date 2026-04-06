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
}
