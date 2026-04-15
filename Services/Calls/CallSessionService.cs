using System.Collections.Concurrent;
using JaeZoo.Server.Models.Calls;

namespace JaeZoo.Server.Services.Calls;

public sealed class CallSessionService
{
    private readonly ConcurrentDictionary<Guid, CallSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _userLastSeenUtc = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _userConnections = new();
    private readonly object _sync = new();

    public CallSession Create(Guid callerUserId, Guid calleeUserId, Guid? dialogId, CallType type, string? callerClientVersion, string? callerDeviceInfo)
    {
        var session = new CallSession
        {
            CallId = Guid.NewGuid(),
            CallerUserId = callerUserId,
            CalleeUserId = calleeUserId,
            DialogId = dialogId,
            Type = type,
            State = CallState.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            CallerClientVersion = callerClientVersion,
            CallerDeviceInfo = callerDeviceInfo,
            CorrelationId = Guid.NewGuid().ToString("N")
        };

        if (!_sessions.TryAdd(session.CallId, session))
            throw new InvalidOperationException("Failed to create call session.");

        TouchUser(callerUserId);
        TouchUser(calleeUserId);
        return session;
    }

    public bool TryCreateIfUsersAvailable(
        Guid callerUserId,
        Guid calleeUserId,
        Guid? dialogId,
        CallType type,
        string? callerClientVersion,
        string? callerDeviceInfo,
        out CallSession? session)
    {
        lock (_sync)
        {
            if (HasActiveCall_NoLock(callerUserId) || HasActiveCall_NoLock(calleeUserId))
            {
                session = null;
                return false;
            }

            session = Create(callerUserId, calleeUserId, dialogId, type, callerClientVersion, callerDeviceInfo);
            return true;
        }
    }

    public bool TryGet(Guid callId, out CallSession? session) => _sessions.TryGetValue(callId, out session);

    public IReadOnlyCollection<CallSession> GetActiveForUser(Guid userId) =>
        _sessions.Values.Where(x => (x.CallerUserId == userId || x.CalleeUserId == userId) && IsActiveState(x.State)).ToArray();

    public bool HasActiveCall(Guid userId)
    {
        lock (_sync)
        {
            return HasActiveCall_NoLock(userId);
        }
    }

    public IReadOnlyCollection<CallSession> GetAll() => _sessions.Values.ToArray();

    public bool IsParticipant(CallSession session, Guid userId) => session.CallerUserId == userId || session.CalleeUserId == userId;

    public Guid GetPeerId(CallSession session, Guid userId) => session.CallerUserId == userId ? session.CalleeUserId : session.CallerUserId;

    public CallSession Update(Guid callId, Action<CallSession> apply)
    {
        if (!_sessions.TryGetValue(callId, out var session) || session is null)
            throw new KeyNotFoundException($"Call session {callId} was not found.");

        lock (session)
        {
            apply(session);
            return session;
        }
    }

    public bool TryRemove(Guid callId, out CallSession? session) => _sessions.TryRemove(callId, out session);

    public void MarkUserConnected(Guid userId, string connectionId)
    {
        var bucket = _userConnections.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        bucket[connectionId] = 0;
        TouchUser(userId);
    }

    public void MarkUserDisconnected(Guid userId, string connectionId)
    {
        if (_userConnections.TryGetValue(userId, out var bucket))
        {
            bucket.TryRemove(connectionId, out _);
            if (bucket.IsEmpty)
                _userConnections.TryRemove(userId, out _);
        }

        TouchUser(userId);
    }

    public void TouchUser(Guid userId) => _userLastSeenUtc[userId] = DateTime.UtcNow;

    public void TouchCallParticipant(Guid callId, Guid userId)
    {
        Update(callId, session =>
        {
            session.LastActivityAtUtc = DateTime.UtcNow;
            if (session.CallerUserId == userId)
                session.LastCallerActivityAtUtc = DateTime.UtcNow;
            else if (session.CalleeUserId == userId)
                session.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });

        TouchUser(userId);
    }

    public bool IsUserOnline(Guid userId) =>
        _userConnections.TryGetValue(userId, out var bucket) && !bucket.IsEmpty;

    public DateTime? GetUserLastSeenUtc(Guid userId) =>
        _userLastSeenUtc.TryGetValue(userId, out var lastSeenUtc) ? lastSeenUtc : null;

    public static bool IsActiveState(CallState state) => state is CallState.Pending or CallState.Ringing or CallState.Accepted or CallState.Connecting or CallState.Connected;

    private bool HasActiveCall_NoLock(Guid userId) =>
        _sessions.Values.Any(x => (x.CallerUserId == userId || x.CalleeUserId == userId) && IsActiveState(x.State));
}
