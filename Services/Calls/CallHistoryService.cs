using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Calls;
using JaeZoo.Server.Services.Chat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Calls;

public sealed class CallHistoryService
{
    private readonly AppDbContext _db;
    private readonly DirectChatService _chat;
    private readonly IHubContext<ChatHub> _chatHub;
    private readonly ILogger<CallHistoryService> _logger;

    public CallHistoryService(
        AppDbContext db,
        DirectChatService chat,
        IHubContext<ChatHub> chatHub,
        ILogger<CallHistoryService> logger)
    {
        _db = db;
        _chat = chat;
        _chatHub = chatHub;
        _logger = logger;
    }

    public async Task<Guid> ResolveDirectDialogIdAsync(Guid callerUserId, Guid calleeUserId, Guid? requestedDialogId, CancellationToken ct)
    {
        if (requestedDialogId.HasValue && requestedDialogId.Value != Guid.Empty)
        {
            var dlg = await _db.DirectDialogs.AsNoTracking().FirstOrDefaultAsync(d => d.Id == requestedDialogId.Value, ct);
            if (dlg is null)
                throw new InvalidOperationException("Direct dialog was not found.");

            var (u1, u2) = DirectChatService.OrderPair(callerUserId, calleeUserId);
            if (dlg.User1Id != u1 || dlg.User2Id != u2)
                throw new InvalidOperationException("Direct dialog does not belong to the call participants.");

            return dlg.Id;
        }

        var resolved = await _chat.GetOrCreateDialogAsync(callerUserId, calleeUserId, ct);
        return resolved.Id;
    }

    public async Task<bool> TryPersistTerminalEventAsync(CallSession session, CancellationToken ct = default)
    {
        if (session.DialogId is null || session.DialogId == Guid.Empty)
            return false;

        lock (session)
        {
            if (session.HistoryPersistedAtUtc.HasValue)
                return false;

            session.HistoryPersistedAtUtc = DateTime.UtcNow;
        }

        try
        {
            var dialog = await _db.DirectDialogs.AsNoTracking().FirstOrDefaultAsync(d => d.Id == session.DialogId.Value, ct);
            if (dialog is null)
                return false;

            var (systemKey, text, senderId) = BuildHistoryEntry(session);
            var recipientUserId = session.CallerUserId == senderId ? session.CalleeUserId : session.CallerUserId;
            var created = await _chat.CreateMessageAsync(senderId, recipientUserId, text, null, DirectMessageKind.System, systemKey, null, ct);
            var dto = await _chat.GetMessageDtoAsync(created.dialog.Id, created.message.Id, ct);
            if (dto is null)
                return false;

            await _chatHub.Clients.User(session.CallerUserId.ToString()).SendAsync("ChatMessageCreated", new ChatRealtimeMessageDto(session.CalleeUserId, dto), ct);
            await _chatHub.Clients.User(session.CalleeUserId.ToString()).SendAsync("ChatMessageCreated", new ChatRealtimeMessageDto(session.CallerUserId, dto), ct);

            try
            {
                var unread = await _chat.GetUnreadForUserAsync(created.dialog, recipientUserId, ct);
                await _chatHub.Clients.User(recipientUserId.ToString()).SendAsync("ChatUnreadChanged", new ChatUnreadChangedDto(senderId, unread.count, unread.firstId, unread.firstAt), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast unread update for call history. dialog={DialogId} call={CallId}", session.DialogId, session.CallId);
            }

            return true;
        }
        catch (Exception ex)
        {
            lock (session)
            {
                session.HistoryPersistedAtUtc = null;
            }
            _logger.LogError(ex, "Failed to persist terminal call event. call={CallId} dialog={DialogId} state={State} reason={Reason}", session.CallId, session.DialogId, session.State, session.EndReason);
            return false;
        }
    }

    private static (string systemKey, string text, Guid senderId) BuildHistoryEntry(CallSession session)
    {
        var duration = session.ConnectedAtUtc.HasValue && session.EndedAtUtc.HasValue && session.EndedAtUtc > session.ConnectedAtUtc
            ? session.EndedAtUtc.Value - session.ConnectedAtUtc.Value
            : (TimeSpan?)null;

        if (session.ConnectedAtUtc.HasValue && session.State is CallState.Ended or CallState.Failed or CallState.TimedOut)
        {
            return ("call.connected", $"📞 Звонок завершён. Длительность: {FormatDuration(duration)}", session.CallerUserId);
        }

        return session.State switch
        {
            CallState.Declined => ("call.declined", "📞 Звонок отклонён", session.CalleeUserId),
            CallState.Busy => ("call.busy", "📞 Пользователь был занят", session.CalleeUserId),
            CallState.Cancelled => ("call.cancelled", "📞 Звонок отменён", session.CallerUserId),
            CallState.Missed => ("call.missed", "📞 Пропущенный звонок", session.CallerUserId),
            CallState.TimedOut when string.Equals(session.EndReason, "missed-timeout", StringComparison.OrdinalIgnoreCase)
                => ("call.missed", "📞 Пропущенный звонок", session.CallerUserId),
            CallState.Failed => ("call.failed", "📞 Не удалось установить соединение", session.CallerUserId),
            CallState.Ended when string.Equals(session.EndReason, "cancelled-by-caller", StringComparison.OrdinalIgnoreCase)
                => ("call.cancelled", "📞 Звонок отменён", session.CallerUserId),
            CallState.Ended => ("call.ended", "📞 Звонок завершён", session.CallerUserId),
            CallState.TimedOut => ("call.timedout", "📞 Время ожидания звонка истекло", session.CallerUserId),
            _ => ("call.ended", "📞 Звонок завершён", session.CallerUserId)
        };
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null || duration <= TimeSpan.Zero)
            return "00:00";

        var d = duration.Value;
        return d.TotalHours >= 1 ? d.ToString(@"hh\:mm\:ss") : d.ToString(@"mm\:ss");
    }
}
