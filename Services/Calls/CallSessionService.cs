using System.Collections.Concurrent;
using System.Text;
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

    public string BuildDebugSnapshot(Guid? focusUserId = null)
    {
        var sb = new StringBuilder();
        sb.Append("sessions=");
        var sessions = _sessions.Values.OrderBy(x => x.CreatedAtUtc).ToArray();
        if (sessions.Length == 0)
        {
            sb.Append("<empty>");
        }
        else
        {
            for (var i = 0; i < sessions.Length; i++)
            {
                if (i > 0) sb.Append(" || ");
                sb.Append(DescribeSession(sessions[i]));
            }
        }

        if (focusUserId.HasValue)
        {
            sb.Append(" | focusUser=").Append(focusUserId.Value);
            sb.Append(" | activeForFocus=");
            var active = GetActiveForUser(focusUserId.Value).Select(x => x.CallId).ToArray();
            sb.Append(active.Length == 0 ? "<none>" : string.Join(',', active));
            sb.Append(" | online=").Append(IsUserOnline(focusUserId.Value));
            sb.Append(" | lastSeenUtc=").Append(GetUserLastSeenUtc(focusUserId.Value)?.ToString("O") ?? "null");
            sb.Append(" | connections=");
            if (_userConnections.TryGetValue(focusUserId.Value, out var bucket) && !bucket.IsEmpty)
                sb.Append(string.Join(',', bucket.Keys.OrderBy(x => x)));
            else
                sb.Append("<none>");
        }

        return sb.ToString();
    }

    public static string DescribeSession(CallSession session)
        => $"callId={session.CallId};state={session.State};caller={session.CallerUserId};callee={session.CalleeUserId};dialogId={session.DialogId};corr={session.CorrelationId};created={session.CreatedAtUtc:O};accepted={session.AcceptedAtUtc:O};connected={session.ConnectedAtUtc:O};ended={session.EndedAtUtc:O};lastActivity={session.LastActivityAtUtc:O};lastCaller={session.LastCallerActivityAtUtc:O};lastCallee={session.LastCalleeActivityAtUtc:O};lastOffer={session.LastOfferAtUtc:O};lastAnswer={session.LastAnswerAtUtc:O};lastIce={session.LastIceCandidateAtUtc:O};endReason={session.EndReason}";


    public DateTime? GetUserLastSeenUtc(Guid userId) =>
        _userLastSeenUtc.TryGetValue(userId, out var lastSeenUtc) ? lastSeenUtc : null;

    public static bool IsActiveState(CallState state) => state is CallState.Pending or CallState.Ringing or CallState.Accepted or CallState.Connecting or CallState.Connected;

    private bool HasActiveCall_NoLock(Guid userId) =>
        _sessions.Values.Any(x => (x.CallerUserId == userId || x.CalleeUserId == userId) && IsActiveState(x.State));
}
