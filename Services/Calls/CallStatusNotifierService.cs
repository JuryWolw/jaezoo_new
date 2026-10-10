using System.Collections.Concurrent;
using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Calls;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Calls;

/// <summary>
/// Статус «в звонке» для списка друзей. Раз в 2 секунды сравнивает, кто сейчас в звонке, и рассылает друзьям
/// только изменения (событие <c>UserCallStatusChanged</c>). Видимость — по настройке самого пользователя:
/// 0 — не показывать, 1 — «в звонке», 2 — «в звонке с ник».
/// </summary>
public sealed class CallStatusNotifierService : BackgroundService
{
    public const string HubEventName = "UserCallStatusChanged";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly CallSessionService _sessions;
    private readonly IHubContext<ChatHub> _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CallStatusNotifierService> _logger;

    /// <summary>Пользователь → с кем он в звонке (по данным последнего опроса).</summary>
    private Dictionary<Guid, Guid> _inCall = new();

    /// <summary>Что сейчас видят друзья: пользователь → имя собеседника (null — просто «в звонке»).</summary>
    private readonly ConcurrentDictionary<Guid, string?> _announced = new();

    public CallStatusNotifierService(CallSessionService sessions, IHubContext<ChatHub> hub, IServiceScopeFactory scopeFactory, ILogger<CallStatusNotifierService> logger)
    {
        _sessions = sessions;
        _hub = hub;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Снимок для клиента при подключении: кто из перечисленных пользователей сейчас в звонке.</summary>
    public IReadOnlyDictionary<Guid, string?> SnapshotFor(IEnumerable<Guid> userIds)
    {
        var result = new Dictionary<Guid, string?>();
        foreach (var id in userIds)
        {
            if (_announced.TryGetValue(id, out var peerName))
                result[id] = peerName;
        }
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Call status notifier tick failed.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var current = new Dictionary<Guid, Guid>();
        foreach (var session in _sessions.GetAll())
        {
            if (session.State is not (CallState.Accepted or CallState.Connecting or CallState.Connected))
                continue;
            current[session.CallerUserId] = session.CalleeUserId;
            current[session.CalleeUserId] = session.CallerUserId;
        }

        var changed = current.Where(p => !_inCall.TryGetValue(p.Key, out var oldPeer) || oldPeer != p.Value).Select(p => p.Key)
            .Concat(_inCall.Keys.Where(id => !current.ContainsKey(id)))
            .Distinct()
            .ToList();
        _inCall = current;
        if (changed.Count == 0)
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var userId in changed)
        {
            var inCall = current.TryGetValue(userId, out var peerId);
            var visibility = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (int?)u.CallStatusVisibility).FirstOrDefaultAsync(ct) ?? 1;

            if (!inCall || visibility <= 0)
            {
                if (_announced.TryRemove(userId, out _))
                    await BroadcastAsync(db, userId, false, null, ct);
                continue;
            }

            string? peerName = null;
            if (visibility >= 2)
            {
                peerName = await db.Users.AsNoTracking().Where(u => u.Id == peerId)
                    .Select(u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName)
                    .FirstOrDefaultAsync(ct);
            }

            _announced[userId] = peerName;
            await BroadcastAsync(db, userId, true, peerName, ct);
        }
    }

    private async Task BroadcastAsync(AppDbContext db, Guid userId, bool inCall, string? peerName, CancellationToken ct)
    {
        var friendIds = await db.Friendships.AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId)
            .Distinct()
            .ToListAsync(ct);
        if (friendIds.Count == 0)
            return;

        await _hub.Clients.Users(friendIds.Select(id => id.ToString()).ToList())
            .SendAsync(HubEventName, new { userId = userId.ToString("D"), inCall, peerName }, ct);
    }
}
