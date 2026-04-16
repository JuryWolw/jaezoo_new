using System.Security.Claims;
using JaeZoo.Server.Models.Calls;
using JaeZoo.Server.Services.Calls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace JaeZoo.Server.Hubs;

[Authorize]
public sealed class CallsHub : Hub
{
    private readonly CallSessionService _sessions;
    private readonly CallAuditService _audit;
    private readonly TurnCredentialsService _turn;
    private readonly CallHistoryService _history;
    private readonly ILogger<CallsHub> _logger;

    public CallsHub(
        CallSessionService sessions,
        CallAuditService audit,
        TurnCredentialsService turn,
        CallHistoryService history,
        ILogger<CallsHub> logger)
    {
        _sessions = sessions;
        _audit = audit;
        _turn = turn;
        _history = history;
        _logger = logger;
    }

    private Guid MeId
    {
        get
        {
            var raw = Context.User?.FindFirst("sub")?.Value
                      ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? Context.User?.FindFirst("uid")?.Value;

            if (!Guid.TryParse(raw, out var id))
                throw new HubException("No user id claim.");

            return id;
        }
    }

    public override Task OnConnectedAsync()
    {
        _sessions.MarkUserConnected(MeId, Context.ConnectionId);
        _logger.LogInformation("CallsHub connected. UserId={UserId} ConnectionId={ConnectionId}", MeId, Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _sessions.MarkUserDisconnected(MeId, Context.ConnectionId);
        _logger.LogInformation(exception, "CallsHub disconnected. UserId={UserId} ConnectionId={ConnectionId}", MeId, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task<IceConfigResponse> GetIceConfig() => Task.FromResult(_turn.CreateForUser(MeId));

    public async Task AcceptCall(AcceptCallRequest request)
    {
        var session = RequireParticipant(request.CallId);
        var me = MeId;

        if (session.CalleeUserId != me)
            throw new HubException("Only callee can accept the call.");

        _sessions.Update(session.CallId, s =>
        {
            EnsureState(s, CallState.Pending, CallState.Ringing);
            s.State = CallState.Accepted;
            s.AcceptedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            s.LastCalleeActivityAtUtc = DateTime.UtcNow;
            s.CalleeClientVersion = request.ClientVersion;
            s.CalleeDeviceInfo = request.DeviceInfo;
        });
        _sessions.TouchUser(me);

        var changed = ToStateChanged(session, CallState.Accepted, null);
        _audit.Info(session, "call.accepted");

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync("call.accepted", changed);
    }

    public async Task DeclineCall(DeclineCallRequest request)
    {
        var session = RequireParticipant(request.CallId);

        _sessions.Update(session.CallId, s =>
        {
            EnsureState(s, CallState.Pending, CallState.Ringing, CallState.Accepted, CallState.Connecting);
            s.State = CallState.Declined;
            s.EndReason = string.IsNullOrWhiteSpace(request.Reason) ? "declined" : request.Reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
        });

        await _history.TryPersistTerminalEventAsync(session);

        var changed = ToStateChanged(session, CallState.Declined, session.EndReason);
        _audit.Info(session, "call.declined", session.EndReason);

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync("call.declined", changed);

        _sessions.TryRemove(session.CallId, out _);
    }

    public async Task Busy(BusyCallRequest request)
    {
        var session = RequireParticipant(request.CallId);
        var me = MeId;
        if (session.CalleeUserId != me)
            throw new HubException("Only callee can report busy for this call.");

        _sessions.Update(session.CallId, s =>
        {
            EnsureState(s, CallState.Pending, CallState.Ringing);
            s.State = CallState.Busy;
            s.EndReason = string.IsNullOrWhiteSpace(request.Reason) ? "busy" : request.Reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
        });

        await _history.TryPersistTerminalEventAsync(session);

        var changed = ToStateChanged(session, CallState.Busy, session.EndReason);
        _audit.Warn(session, "call.busy", session.EndReason);

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync("call.busy", changed);

        _sessions.TryRemove(session.CallId, out _);
    }

    public async Task Hangup(HangupCallRequest request)
    {
        var session = RequireParticipant(request.CallId);
        var me = MeId;

        _sessions.Update(session.CallId, s =>
        {
            EnsureState(s, CallState.Pending, CallState.Ringing, CallState.Accepted, CallState.Connecting, CallState.Connected);
            var normalizedReason = string.IsNullOrWhiteSpace(request.Reason) ? "hangup" : request.Reason;
            s.State = normalizedReason.Contains("cancel", StringComparison.OrdinalIgnoreCase) && !s.ConnectedAtUtc.HasValue
                ? CallState.Cancelled
                : CallState.Ended;
            s.EndReason = normalizedReason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me)
                s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else if (s.CalleeUserId == me)
                s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });
        _sessions.TouchUser(me);

        await _history.TryPersistTerminalEventAsync(session);

        var eventName = session.State == CallState.Cancelled ? "call.cancelled" : "call.ended";
        var changed = ToStateChanged(session, session.State, session.EndReason);
        _audit.Info(session, eventName, session.EndReason);

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync(eventName, changed);

        _sessions.TryRemove(session.CallId, out _);
    }

    public async Task SendOffer(WebRtcOfferDto request)
    {
        var session = RequireParticipant(request.CallId);
        var me = MeId;
        var peer = _sessions.GetPeerId(session, me);

        _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State))
                return;

            if (s.State is CallState.Accepted or CallState.Pending or CallState.Ringing)
                s.State = CallState.Connecting;
            s.LastOfferAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me)
                s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else
                s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });
        _sessions.TouchUser(me);

        _audit.Info(session, "call.offer", extra: new { fromUserId = me, sdpLength = request.Sdp?.Length ?? 0 });

        await Clients.User(peer.ToString()).SendAsync("call.offer", new
        {
            request.CallId,
            request.Type,
            request.Sdp,
            fromUserId = me,
            correlationId = session.CorrelationId
        });
    }

    public async Task SendAnswer(WebRtcAnswerDto request)
    {
        var session = RequireParticipant(request.CallId);
        var me = MeId;
        var peer = _sessions.GetPeerId(session, me);

        _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State))
                return;

            if (s.State is CallState.Accepted or CallState.Pending or CallState.Ringing)
                s.State = CallState.Connecting;
            s.LastAnswerAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me)
                s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else
                s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });
        _sessions.TouchUser(me);

        _audit.Info(session, "call.answer", extra: new { fromUserId = me, sdpLength = request.Sdp?.Length ?? 0 });

        await Clients.User(peer.ToString()).SendAsync("call.answer", new
        {
            request.CallId,
            request.Type,
            request.Sdp,
            fromUserId = me,
            correlationId = session.CorrelationId
        });
    }

    public async Task SendIceCandidate(Guid callId, string? candidate, string? sdpMid, int? sdpMLineIndex, string? usernameFragment)
    {
        var session = RequireParticipant(callId);
        var me = MeId;
        var peer = _sessions.GetPeerId(session, me);

        _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State))
                return;

            if (s.State is CallState.Accepted or CallState.Pending or CallState.Ringing)
                s.State = CallState.Connecting;
            s.LastIceCandidateAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me)
                s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else
                s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });
        _sessions.TouchUser(me);

        _audit.Info(session, "call.ice-candidate", extra: new { fromUserId = me, sdpMid, sdpMLineIndex, hasCandidate = !string.IsNullOrWhiteSpace(candidate) });

        await Clients.User(peer.ToString()).SendAsync("call.ice-candidate", new
        {
            callId,
            candidate,
            sdpMid,
            sdpMLineIndex,
            usernameFragment,
            fromUserId = me,
            correlationId = session.CorrelationId
        });
    }

    public async Task MarkConnected(Guid callId)
    {
        var session = RequireParticipant(callId);
        var me = MeId;

        _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State))
                return;

            s.State = CallState.Connected;
            s.ConnectedAtUtc ??= DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me)
                s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else
                s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });
        _sessions.TouchUser(me);

        var changed = ToStateChanged(session, CallState.Connected, null);
        _audit.Info(session, "call.connected");

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync("call.connected", changed);
    }

    public async Task ReportFailure(Guid callId, string reason)
    {
        var session = RequireParticipant(callId);
        var me = MeId;

        _sessions.Update(session.CallId, s =>
        {
            EnsureState(s, CallState.Pending, CallState.Ringing, CallState.Accepted, CallState.Connecting, CallState.Connected);
            s.State = CallState.Failed;
            s.EndReason = string.IsNullOrWhiteSpace(reason) ? "failed" : reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me)
                s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else
                s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });
        _sessions.TouchUser(me);

        await _history.TryPersistTerminalEventAsync(session);

        var changed = ToStateChanged(session, CallState.Failed, session.EndReason);
        _audit.Warn(session, "call.failed", session.EndReason);

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync("call.failed", changed);

        _sessions.TryRemove(session.CallId, out _);
    }

    public Task HeartbeatCall(Guid callId)
    {
        var session = RequireParticipant(callId);
        _sessions.TouchCallParticipant(callId, MeId);
        _audit.Info(session, "call.heartbeat", extra: new { connectionId = Context.ConnectionId, userId = MeId });
        return Task.CompletedTask;
    }

    private CallSession RequireParticipant(Guid callId)
    {
        if (!_sessions.TryGet(callId, out var session) || session is null)
            throw new HubException("Call session not found.");

        if (!_sessions.IsParticipant(session, MeId))
            throw new HubException("User is not a participant of this call.");

        return session;
    }

    private static void EnsureState(CallSession session, params CallState[] allowedStates)
    {
        if (allowedStates.Contains(session.State))
            return;

        throw new HubException($"Invalid call state transition from '{session.State}'.");
    }

    private static CallStateChangedDto ToStateChanged(CallSession session, CallState state, string? reason)
        => new(
            session.CallId,
            session.CallerUserId,
            session.CalleeUserId,
            session.DialogId,
            session.Type,
            state,
            DateTime.UtcNow,
            reason,
            session.CorrelationId);
}
