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

    public ChatHub(AppDbContext db, IPresenceTracker presence, ILogger<ChatHub> log, DirectChatService chat)
    {
        _db = db;
        _presence = presence;
        _log = log;
        _chat = chat;
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
}
