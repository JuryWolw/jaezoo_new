using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace JaeZoo.Server.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly AppDbContext _db;
    private readonly IPresenceTracker _presence;
    private readonly ILogger<ChatHub> _log;
    private readonly DirectChatService _chat;
    private readonly GroupChatService _groupChats;

    public ChatHub(AppDbContext db, IPresenceTracker presence, ILogger<ChatHub> log, DirectChatService chat, GroupChatService groupChats)
    {
        _db = db;
        _presence = presence;
        _log = log;
        _chat = chat;
        _groupChats = groupChats;
    }

    private Guid MeId
    {
        get
        {
            var s = Context.User?.FindFirst("sub")?.Value
                    ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? Context.User?.FindFirst("uid")?.Value;

            if (!Guid.TryParse(s, out var id))
                throw new HubException("No user id claim.");

            return id;
        }
    }

    public override async Task OnConnectedAsync()
    {
        var userId = MeId.ToString();
        var first = await _presence.UserConnected(userId, Context.ConnectionId);

        if (first)
        {
            var canShow = await _db.Users
                .Where(u => u.Id == MeId)
                .Select(u => u.ShowOnline)
                .FirstOrDefaultAsync();

            if (canShow)
                await Clients.AllExcept(Context.ConnectionId).SendAsync("UserOnline", userId);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = MeId.ToString();
        var last = await _presence.UserDisconnected(userId, Context.ConnectionId);

        if (last)
        {
            var canShow = await _db.Users
                .Where(u => u.Id == MeId)
                .Select(u => u.ShowOnline)
                .FirstOrDefaultAsync();

            if (canShow)
                await Clients.All.SendAsync("UserOffline", userId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task<List<string>> GetOnlineUsers()
    {
        var online = await _presence.GetOnlineUsers();
        if (online.Count == 0) return online;

        var onlineGuids = online.Select(Guid.Parse).ToList();
        var visible = await _db.Users
            .Where(u => onlineGuids.Contains(u.Id) && u.ShowOnline)
            .Select(u => u.Id.ToString())
            .ToListAsync();

        visible.Sort(StringComparer.Ordinal);
        return visible;
    }

    public async Task SendMessage(Guid targetUserId, SendMessageRequest request)
    {
        try
        {
            var me = MeId;
            request ??= new SendMessageRequest(null, null);

            if (!await _chat.AreFriends(me, targetUserId, Context.ConnectionAborted))
                throw new HubException("Вы не друзья.");

            var created = await _chat.CreateMessageAsync(
                me,
                targetUserId,
                request.Text,
                request.FileIds,
                DirectMessageKind.User,
                null,
                null,
                Context.ConnectionAborted);

            var dto = await _chat.GetMessageDtoAsync(created.dialog.Id, created.message.Id, Context.ConnectionAborted);
            if (dto is null)
                throw new HubException("Не удалось сформировать сообщение.");

            await EmitCreatedToParticipants(me, targetUserId, dto, created.dialog, Context.ConnectionAborted);
        }
        catch (InvalidOperationException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (HubException)
        {
            throw;
        }
        catch (DbUpdateException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            _log.LogError(ex, "SendMessage db update failed. target={Target}. detail={Detail}", targetUserId, detail);
            throw new HubException($"SendMessage failed: DbUpdateException: {detail}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendMessage failed. target={Target}", targetUserId);
            throw new HubException($"SendMessage failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task SendDirectMessage(Guid targetUserId, string text)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        await SendMessage(targetUserId, new SendMessageRequest(text));
    }

    public async Task SendDirectMessageWithFiles(Guid targetUserId, string? text, List<Guid>? fileIds)
    {
        await SendMessage(targetUserId, new SendMessageRequest(text, fileIds));
    }

    public async Task ForwardMessages(Guid targetUserId, ForwardMessagesRequest request)
    {
        try
        {
            var me = MeId;
            if (request?.MessageIds is null || request.MessageIds.Count == 0)
                throw new HubException("MessageIds are required.");
            if (!await _chat.AreFriends(me, targetUserId, Context.ConnectionAborted))
                throw new HubException("Вы не друзья.");

            var ids = request.MessageIds.Where(x => x != Guid.Empty).Distinct().Take(20).ToList();
            var sources = await (
                from m in _db.DirectMessages.AsNoTracking()
                join d in _db.DirectDialogs.AsNoTracking() on m.DialogId equals d.Id
                where ids.Contains(m.Id)
                      && m.DeletedAt == null
                      && (d.User1Id == me || d.User2Id == me)
                orderby m.SentAt, m.Id
                select m
            ).ToListAsync(Context.ConnectionAborted);

            foreach (var source in sources)
            {
                var created = await _chat.ForwardMessageAsync(me, targetUserId, source, request.IncludeAttachments, Context.ConnectionAborted);
                var dto = await _chat.GetMessageDtoAsync(created.dialog.Id, created.message.Id, Context.ConnectionAborted);
                if (dto is not null)
                    await EmitCreatedToParticipants(me, targetUserId, dto, created.dialog, Context.ConnectionAborted);
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task SendSystemMessage(Guid targetUserId, string systemKey, string? text)
    {
        try
        {
            var me = MeId;
            if (string.IsNullOrWhiteSpace(systemKey))
                throw new HubException("SystemKey is required.");
            if (!await _chat.AreFriends(me, targetUserId, Context.ConnectionAborted))
                throw new HubException("Вы не друзья.");

            var created = await _chat.CreateMessageAsync(me, targetUserId, text, null, DirectMessageKind.System, systemKey, null, Context.ConnectionAborted);
            var dto = await _chat.GetMessageDtoAsync(created.dialog.Id, created.message.Id, Context.ConnectionAborted);
            if (dto is null)
                throw new HubException("Не удалось сформировать системное сообщение.");
            await EmitCreatedToParticipants(me, targetUserId, dto, created.dialog, Context.ConnectionAborted);
        }
        catch (InvalidOperationException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    private async Task EmitCreatedToParticipants(Guid senderId, Guid targetUserId, MessageDto dto, DirectDialog dlg, CancellationToken ct)
    {
        await Clients.User(senderId.ToString()).SendAsync("ChatMessageCreated", new ChatRealtimeMessageDto(targetUserId, dto), ct);
        await Clients.User(targetUserId.ToString()).SendAsync("ChatMessageCreated", new ChatRealtimeMessageDto(senderId, dto), ct);

        try
        {
            var unread = await _chat.GetUnreadForUserAsync(dlg, targetUserId, ct);
            var typedUnread = new ChatUnreadChangedDto(senderId, unread.count, unread.firstId, unread.firstAt);

            await Clients.User(targetUserId.ToString()).SendAsync("ChatUnreadChanged", typedUnread, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ChatUnreadChanged failed. me={Me} target={Target} dialog={Dialog}", senderId, targetUserId, dlg.Id);
        }
    }


    public async Task SendGroupMessage(Guid groupId, SendMessageRequest request)
    {
        try
        {
            var me = MeId;
            request ??= new SendMessageRequest(null, null);

            var created = await _groupChats.CreateMessageAsync(me, groupId, request.Text, request.FileIds, DirectMessageKind.User, null, null, Context.ConnectionAborted);
            var dto = await _groupChats.GetMessageDtoAsync(groupId, created.message.Id, Context.ConnectionAborted);
            if (dto is null)
                throw new HubException("Не удалось сформировать сообщение.");

            await EmitCreatedToGroup(groupId, dto, me, Context.ConnectionAborted);
        }
        catch (InvalidOperationException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (HubException)
        {
            throw;
        }
        catch (DbUpdateException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            _log.LogError(ex, "SendGroupMessage db update failed. group={GroupId}. detail={Detail}", groupId, detail);
            throw new HubException($"SendGroupMessage failed: DbUpdateException: {detail}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendGroupMessage failed. group={GroupId}", groupId);
            throw new HubException($"SendGroupMessage failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task SendGroupSystemMessage(Guid groupId, string systemKey, string? text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(systemKey))
                throw new HubException("SystemKey is required.");

            var me = MeId;
            var created = await _groupChats.CreateMessageAsync(me, groupId, text, null, DirectMessageKind.System, systemKey, null, Context.ConnectionAborted);
            var dto = await _groupChats.GetMessageDtoAsync(groupId, created.message.Id, Context.ConnectionAborted);
            if (dto is null)
                throw new HubException("Не удалось сформировать системное сообщение.");

            await EmitCreatedToGroup(groupId, dto, me, Context.ConnectionAborted);
        }
        catch (InvalidOperationException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task ForwardGroupMessages(Guid groupId, ForwardMessagesRequest request)
    {
        try
        {
            var me = MeId;
            if (request?.MessageIds is null || request.MessageIds.Count == 0)
                throw new HubException("MessageIds are required.");
            if (!await _groupChats.IsMemberAsync(groupId, me, Context.ConnectionAborted))
                throw new HubException("Групповой чат не найден.");

            var ids = request.MessageIds.Where(x => x != Guid.Empty).Distinct().Take(20).ToList();
            var sources = await _db.GroupMessages.AsNoTracking()
                .Where(m => ids.Contains(m.Id) && m.DeletedAt == null)
                .Join(_db.GroupChatMembers.AsNoTracking().Where(m => m.UserId == me), m => m.GroupChatId, gm => gm.GroupChatId, (m, gm) => m)
                .OrderBy(m => m.SentAt)
                .ThenBy(m => m.Id)
                .ToListAsync(Context.ConnectionAborted);

            foreach (var source in sources)
            {
                var created = await _groupChats.ForwardMessageAsync(me, groupId, source, request.IncludeAttachments, Context.ConnectionAborted);
                var dto = await _groupChats.GetMessageDtoAsync(groupId, created.message.Id, Context.ConnectionAborted);
                if (dto is not null)
                    await EmitCreatedToGroup(groupId, dto, me, Context.ConnectionAborted);
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task ForwardMessagesToDirect(Guid targetUserId, CrossChatForwardRequest request)
    {
        try
        {
            var me = MeId;
            if (request?.MessageIds is null || request.MessageIds.Count == 0)
                throw new HubException("MessageIds are required.");
            if (!await _chat.AreFriends(me, targetUserId, Context.ConnectionAborted))
                throw new HubException("Вы не друзья.");

            var ids = request.MessageIds.Where(x => x != Guid.Empty).Distinct().Take(20).ToList();
            var source = (request.Source ?? string.Empty).Trim().ToLowerInvariant();

            if (source == "group")
            {
                var sourcesQuery = _db.GroupMessages.AsNoTracking()
                    .Where(m => ids.Contains(m.Id) && m.DeletedAt == null)
                    .Join(_db.GroupChatMembers.AsNoTracking().Where(m => m.UserId == me), m => m.GroupChatId, gm => gm.GroupChatId, (m, gm) => m);

                if (request.SourceChatId.HasValue && request.SourceChatId.Value != Guid.Empty)
                    sourcesQuery = sourcesQuery.Where(m => m.GroupChatId == request.SourceChatId.Value);

                var sources = await sourcesQuery.OrderBy(m => m.SentAt).ThenBy(m => m.Id).ToListAsync(Context.ConnectionAborted);
                foreach (var sourceMessage in sources)
                {
                    var created = await _chat.ForwardMessageAsync(me, targetUserId, sourceMessage, request.IncludeAttachments, Context.ConnectionAborted);
                    var dto = await _chat.GetMessageDtoAsync(created.dialog.Id, created.message.Id, Context.ConnectionAborted);
                    if (dto is not null)
                        await EmitCreatedToParticipants(me, targetUserId, dto, created.dialog, Context.ConnectionAborted);
                }
            }
            else if (source == "direct")
            {
                await ForwardMessages(targetUserId, new ForwardMessagesRequest(ids, request.IncludeAttachments));
            }
            else
            {
                throw new HubException("Source must be 'direct' or 'group'.");
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task ForwardMessagesToGroup(Guid groupId, CrossChatForwardRequest request)
    {
        try
        {
            var me = MeId;
            if (request?.MessageIds is null || request.MessageIds.Count == 0)
                throw new HubException("MessageIds are required.");
            if (!await _groupChats.IsMemberAsync(groupId, me, Context.ConnectionAborted))
                throw new HubException("Групповой чат не найден.");

            var ids = request.MessageIds.Where(x => x != Guid.Empty).Distinct().Take(20).ToList();
            var source = (request.Source ?? string.Empty).Trim().ToLowerInvariant();

            if (source == "group")
            {
                var sourcesQuery = _db.GroupMessages.AsNoTracking()
                    .Where(m => ids.Contains(m.Id) && m.DeletedAt == null)
                    .Join(_db.GroupChatMembers.AsNoTracking().Where(m => m.UserId == me), m => m.GroupChatId, gm => gm.GroupChatId, (m, gm) => m);

                if (request.SourceChatId.HasValue && request.SourceChatId.Value != Guid.Empty)
                    sourcesQuery = sourcesQuery.Where(m => m.GroupChatId == request.SourceChatId.Value);

                var sources = await sourcesQuery.OrderBy(m => m.SentAt).ThenBy(m => m.Id).ToListAsync(Context.ConnectionAborted);
                foreach (var sourceMessage in sources)
                {
                    var created = await _groupChats.ForwardMessageAsync(me, groupId, sourceMessage, request.IncludeAttachments, Context.ConnectionAborted);
                    var dto = await _groupChats.GetMessageDtoAsync(groupId, created.message.Id, Context.ConnectionAborted);
                    if (dto is not null)
                        await EmitCreatedToGroup(groupId, dto, me, Context.ConnectionAborted);
                }
            }
            else if (source == "direct")
            {
                var sources = await (
                    from m in _db.DirectMessages.AsNoTracking()
                    join d in _db.DirectDialogs.AsNoTracking() on m.DialogId equals d.Id
                    where ids.Contains(m.Id) && m.DeletedAt == null && (d.User1Id == me || d.User2Id == me)
                    orderby m.SentAt, m.Id
                    select m
                ).ToListAsync(Context.ConnectionAborted);

                foreach (var sourceMessage in sources)
                {
                    var created = await _groupChats.ForwardMessageAsync(me, groupId, sourceMessage, request.IncludeAttachments, Context.ConnectionAborted);
                    var dto = await _groupChats.GetMessageDtoAsync(groupId, created.message.Id, Context.ConnectionAborted);
                    if (dto is not null)
                        await EmitCreatedToGroup(groupId, dto, me, Context.ConnectionAborted);
                }
            }
            else
            {
                throw new HubException("Source must be 'direct' or 'group'.");
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    private async Task EmitCreatedToGroup(Guid groupId, MessageDto dto, Guid senderId, CancellationToken ct)
    {
        var memberIds = await _db.GroupChatMembers.AsNoTracking().Where(m => m.GroupChatId == groupId).Select(m => m.UserId).ToListAsync(ct);
        foreach (var memberId in memberIds)
        {
            await Clients.User(memberId.ToString()).SendAsync("GroupChatMessageCreated", new GroupChatRealtimeMessageDto(groupId, dto), ct);
        }

        foreach (var memberId in memberIds.Where(x => x != senderId))
        {
            try
            {
                var unread = await _groupChats.GetUnreadForUserAsync(groupId, memberId, ct);
                await Clients.User(memberId.ToString()).SendAsync("GroupChatUnreadChanged", new GroupChatUnreadChangedDto(groupId, unread.count, unread.firstId, unread.firstAt), ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GroupChatUnreadChanged failed. sender={Sender} member={Member} group={GroupId}", senderId, memberId, groupId);
            }
        }
    }

}
