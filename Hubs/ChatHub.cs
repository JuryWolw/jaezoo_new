using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace JaeZoo.Server.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly AppDbContext _db;
    private readonly IPresenceTracker _presence;
    private readonly ILogger<ChatHub> _log;


    public ChatHub(AppDbContext db, IPresenceTracker presence, ILogger<ChatHub> log)
    {
        _db = db;
        _presence = presence;
        _log = log;
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

    private static (Guid a, Guid b) OrderPair(Guid x, Guid y) => x < y ? (x, y) : (y, x);

    private async Task<bool> AreFriends(Guid me, Guid other) =>
        await _db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == me && f.AddresseeId == other) ||
             (f.RequesterId == other && f.AddresseeId == me)));

    /// <summary>
    /// Безопасно "get or create" диалога: если два потока создали одновременно,
    /// уникальный индекс сработает — мы просто перечитаем существующий диалог.
    /// </summary>
    private async Task<DirectDialog> GetOrCreateDialog(Guid aId, Guid bId)
    {
        var (u1, u2) = OrderPair(aId, bId);

        var dlg = await _db.DirectDialogs.FirstOrDefaultAsync(d => d.User1Id == u1 && d.User2Id == u2);
        if (dlg is not null) return dlg;

        dlg = new DirectDialog
        {
            User1Id = u1,
            User2Id = u2,
            LastReadAtUser1 = DateTime.MinValue,
            LastReadMessageIdUser1 = Guid.Empty,
            LastReadAtUser2 = DateTime.MinValue,
            LastReadMessageIdUser2 = Guid.Empty
        };
        _db.DirectDialogs.Add(dlg);

        try
        {
            await _db.SaveChangesAsync();
            return dlg;
        }
        catch (DbUpdateException)
        {
            // гонка: кто-то создал диалог раньше
            var existing = await _db.DirectDialogs.FirstOrDefaultAsync(d => d.User1Id == u1 && d.User2Id == u2);
            if (existing is not null) return existing;
            throw;
        }
    }

    private static (DateTime at, Guid id) GetReadCursor(DirectDialog dlg, Guid userId)
    {
        if (userId == dlg.User1Id) return (dlg.LastReadAtUser1, dlg.LastReadMessageIdUser1);
        if (userId == dlg.User2Id) return (dlg.LastReadAtUser2, dlg.LastReadMessageIdUser2);
        return (DateTime.MinValue, Guid.Empty);
    }

    private async Task<(int count, Guid? firstId, DateTime? firstAt)> GetUnreadForUserAsync(DirectDialog dlg, Guid userId, CancellationToken ct)
    {
        var (at, id) = GetReadCursor(dlg, userId);

        var q = _db.DirectMessages
            .AsNoTracking()
            .Where(m => m.DialogId == dlg.Id && m.SenderId != userId)
            .Where(m => m.SentAt > at || (m.SentAt == at && m.Id.CompareTo(id) > 0));

        var count = await q.CountAsync(ct);
        if (count == 0) return (0, null, null);

        var first = await q
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id)
            .Select(m => new { m.Id, m.SentAt })
            .FirstOrDefaultAsync(ct);

        return (count, first?.Id, first?.SentAt);
    }

    // ===== Presence (с учётом ShowOnline) =====

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

    /// <summary>Список userId, которые и подключены, и не скрывают присутствие.</summary>
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

    // ===== Direct messages =====

    public async Task SendDirectMessage(Guid targetUserId, string text)
    {
        try
        {
            text = (text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            var me = MeId;

            if (!await AreFriends(me, targetUserId))
                throw new HubException("Вы не друзья.");

            var dlg = await GetOrCreateDialog(me, targetUserId);

            var msg = new DirectMessage
            {
                DialogId = dlg.Id,
                SenderId = me,
                Text = text,
                SentAt = DateTime.UtcNow
            };

            _db.DirectMessages.Add(msg);
            await _db.SaveChangesAsync();

            // сообщение получат оба (и sender, и receiver). Важно: peerId различается для каждого получателя
            var payloadForSender = new
            {
                peerId = targetUserId,
                messageId = msg.Id,
                senderId = msg.SenderId,
                text = msg.Text,
                sentAt = msg.SentAt
            };

            var payloadForReceiver = new
            {
                peerId = me,
                messageId = msg.Id,
                senderId = msg.SenderId,
                text = msg.Text,
                sentAt = msg.SentAt
            };

            await Clients.User(me.ToString()).SendAsync("ReceiveDirectMessage", payloadForSender);
            await Clients.User(targetUserId.ToString()).SendAsync("ReceiveDirectMessage", payloadForReceiver);

            // обновляем счётчик непрочитанных для получателя (лампочка + "Новое")
            try
            {
                var ct = Context.ConnectionAborted;
                var (count, firstId, firstAt) = await GetUnreadForUserAsync(dlg, targetUserId, ct);

                await Clients.User(targetUserId.ToString()).SendAsync("UnreadChanged", new
                {
                    friendId = me,
                    unreadCount = count,
                    firstUnreadId = firstId,
                    firstUnreadAt = firstAt
                }, ct);
            }
            catch (Exception ex)
            {
                // не валим отправку сообщения из-за уведомлений
                _log.LogWarning(ex, "UnreadChanged failed. me={Me} target={Target} dialog={Dialog}", me, targetUserId, dlg.Id);
            }
        }
        catch (HubException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Это и есть причина твоего "unexpected error invoking SendDirectMessage".
            // Временно отдаём текст ошибки клиенту + логируем, чтобы быстро починить Render.
            _log.LogError(ex, "SendDirectMessage failed. target={Target}", targetUserId);
            throw new HubException($"SendDirectMessage failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
