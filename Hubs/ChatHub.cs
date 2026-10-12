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

    private static bool IsKnownDirectSystemKey(string? systemKey) => systemKey is
        "call.connected" or
        "call.declined" or
        "call.busy" or
        "call.cancelled" or
        "call.missed" or
        "call.failed" or
        "call.ended" or
        "call.timedout";

    private static bool IsKnownGroupSystemKey(string? systemKey) => systemKey is
        GroupChatService.SystemUserAddedKey or
        GroupChatService.SystemGroupCallStartedKey or
        GroupChatService.SystemGroupCallEndedKey;

    private async Task<bool> CanCreateGroupSystemMessageAsync(Guid groupId, Guid userId, CancellationToken ct)
    {
        var group = await _db.GroupChats.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null) return false;

        var member = await _db.GroupChatMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == userId, ct);
        if (member is null) return false;

        return group.OwnerId == userId || member.Role == GroupChatRole.Admin;
    }


    private async Task EnsureUserEmailConfirmedAsync(Guid userId, string message, CancellationToken ct)
    {
        var confirmed = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.EmailConfirmed)
            .FirstOrDefaultAsync(ct);
        if (!confirmed)
            throw new HubException(message);
    }

    private async Task EnsureDirectChatWritableAsync(Guid me, Guid targetUserId, CancellationToken ct)
    {
        if (!await _chat.AreFriends(me, targetUserId, ct))
            throw new HubException("Вы не друзья.");
        await EnsureUserEmailConfirmedAsync(me, "Подтверди почту, чтобы отправлять сообщения.", ct);
        await EnsureUserEmailConfirmedAsync(targetUserId, "Собеседник ещё не подтвердил почту.", ct);
    }

    private async Task EnsureGroupChatWritableAsync(Guid me, CancellationToken ct)
    {
        await EnsureUserEmailConfirmedAsync(me, "Подтверди почту, чтобы отправлять сообщения.", ct);
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

    public async Task SetTyping(Guid targetUserId, bool isTyping)
    {
        var me = MeId;
        if (targetUserId == Guid.Empty || targetUserId == me)
            return;

        if (!await _chat.AreFriends(me, targetUserId, Context.ConnectionAborted))
            throw new HubException("Вы не друзья.");

        await Clients.User(targetUserId.ToString()).SendAsync(
            "TypingChanged",
            me.ToString("D"),
            isTyping,
            Context.ConnectionAborted);
    }

    public Task TypingStarted(Guid targetUserId) => SetTyping(targetUserId, true);
    public Task TypingStopped(Guid targetUserId) => SetTyping(targetUserId, false);
    public Task UserTyping(Guid targetUserId) => SetTyping(targetUserId, true);
    public Task UserStoppedTyping(Guid targetUserId) => SetTyping(targetUserId, false);
    public Task SendTyping(Guid targetUserId, bool isTyping) => SetTyping(targetUserId, isTyping);
    public Task NotifyTyping(Guid targetUserId, bool isTyping) => SetTyping(targetUserId, isTyping);
    public Task UpdateTyping(Guid targetUserId, bool isTyping) => SetTyping(targetUserId, isTyping);
    public Task SendTypingStatus(Guid targetUserId, bool isTyping) => SetTyping(targetUserId, isTyping);
    public Task TypingChanged(Guid targetUserId, bool isTyping) => SetTyping(targetUserId, isTyping);
    public Task Typing(Guid targetUserId, bool isTyping) => SetTyping(targetUserId, isTyping);
    public Task DirectTyping(Guid targetUserId, bool isTyping) => SetTyping(targetUserId, isTyping);
    public Task SendDirectTyping(Guid targetUserId, bool isTyping) => SetTyping(targetUserId, isTyping);

    public async Task SetGroupTyping(Guid groupId, bool isTyping)
    {
        var me = MeId;
        if (groupId == Guid.Empty)
            return;

        if (!await _groupChats.IsMemberAsync(groupId, me, Context.ConnectionAborted))
            throw new HubException("Групповой чат не найден.");

        var meUser = await _db.Users.AsNoTracking()
            .Where(u => u.Id == me)
            .Select(u => new { u.DisplayName, u.UserName })
            .FirstOrDefaultAsync(Context.ConnectionAborted);

        var displayName = meUser is null
            ? "пользователь"
            : (string.IsNullOrWhiteSpace(meUser.DisplayName) ? meUser.UserName : meUser.DisplayName!);

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = "пользователь";

        var memberIds = await _db.GroupChatMembers.AsNoTracking()
            .Where(m => m.GroupChatId == groupId && m.UserId != me)
            .Select(m => m.UserId)
            .ToListAsync(Context.ConnectionAborted);

        foreach (var memberId in memberIds)
        {
            await Clients.User(memberId.ToString()).SendAsync(
                "GroupTypingChanged",
                groupId.ToString("D"),
                me.ToString("D"),
                displayName,
                isTyping,
                Context.ConnectionAborted);
        }
    }

    public Task GroupTypingStarted(Guid groupId) => SetGroupTyping(groupId, true);
    public Task GroupTypingStopped(Guid groupId) => SetGroupTyping(groupId, false);
    public Task GroupUserTyping(Guid groupId) => SetGroupTyping(groupId, true);
    public Task GroupUserStoppedTyping(Guid groupId) => SetGroupTyping(groupId, false);
    public Task SendGroupTyping(Guid groupId, bool isTyping) => SetGroupTyping(groupId, isTyping);
    public Task NotifyGroupTyping(Guid groupId, bool isTyping) => SetGroupTyping(groupId, isTyping);
    public Task GroupTypingChanged(Guid groupId, bool isTyping) => SetGroupTyping(groupId, isTyping);
    public Task GroupTyping(Guid groupId, bool isTyping) => SetGroupTyping(groupId, isTyping);
    public Task SendGroupTypingStatus(Guid groupId, bool isTyping) => SetGroupTyping(groupId, isTyping);

    public async Task SendSystemMessage(Guid targetUserId, string systemKey, string? text)
    {
        try
        {
            var me = MeId;
            if (string.IsNullOrWhiteSpace(systemKey))
                throw new HubException("SystemKey is required.");
            if (!IsKnownDirectSystemKey(systemKey))
                throw new HubException("Unknown system message key.");
            await EnsureDirectChatWritableAsync(me, targetUserId, Context.ConnectionAborted);

            var created = await _chat.CreateSystemMessageAsync(me, targetUserId, text, systemKey, null, Context.ConnectionAborted);
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

    public async Task SendGroupSystemMessage(Guid groupId, string systemKey, string? text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(systemKey))
                throw new HubException("SystemKey is required.");
            if (!IsKnownGroupSystemKey(systemKey))
                throw new HubException("Unknown system message key.");

            var me = MeId;
            await EnsureGroupChatWritableAsync(me, Context.ConnectionAborted);
            if (!await CanCreateGroupSystemMessageAsync(groupId, me, Context.ConnectionAborted))
                throw new HubException("Only the owner or admin can create system messages.");

            var cleanText = CleanSystemMessageText(text);
            if (string.IsNullOrWhiteSpace(cleanText))
            {
                cleanText = systemKey switch
                {
                    GroupChatService.SystemUserAddedKey => "Пользователь добавлен в группу.",
                    GroupChatService.SystemGroupCallStartedKey => "Пользователь открыл групповой канал.",
                    GroupChatService.SystemGroupCallEndedKey => "Групповой звонок закончился.",
                    _ => "Системное сообщение."
                };
            }

            var created = await _groupChats.CreateSystemMessageAsync(me, groupId, systemKey, cleanText, Context.ConnectionAborted);
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
