using System.Collections.Concurrent;
using JaeZoo.Server.Models.Calls;

namespace JaeZoo.Server.Services.Calls;

public sealed class CallSessionService
{
    private readonly ConcurrentDictionary<Guid, CallSession> _sessions = new();

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

        return session;
    }

    public bool TryGet(Guid callId, out CallSession? session) => _sessions.TryGetValue(callId, out session);

    public IReadOnlyCollection<CallSession> GetActiveForUser(Guid userId) =>
        _sessions.Values.Where(x => (x.CallerUserId == userId || x.CalleeUserId == userId) && IsActiveState(x.State)).ToArray();

    public bool HasActiveCall(Guid userId) => GetActiveForUser(userId).Count > 0;

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

    public static bool IsActiveState(CallState state) => state is CallState.Pending or CallState.Ringing or CallState.Accepted or CallState.Connecting or CallState.Connected;
}
