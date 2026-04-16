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
        RequireCallId(request.CallId, nameof(request.CallId));

        var me = MeId;
        var session = RequireParticipant(request.CallId);

        if (session.CalleeUserId != me)
            throw new HubException("Only callee can accept the call.");

        var updated = _sessions.Update(session.CallId, s =>
        {
            if (s.State == CallState.Accepted)
                return;

            EnsureState(s, CallState.Pending, CallState.Ringing);
            s.State = CallState.Accepted;
            s.AcceptedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            s.LastCalleeActivityAtUtc = DateTime.UtcNow;
            s.CalleeClientVersion = request.ClientVersion;
            s.CalleeDeviceInfo = request.DeviceInfo;
        });

        _sessions.TouchUser(me);

        var changed = ToStateChanged(updated, updated.State, null);
        _audit.Info(updated, "call.accepted");
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString())
            .SendAsync("call.accepted", changed);
    }

    public async Task DeclineCall(DeclineCallRequest request)
    {
        RequireCallId(request.CallId, nameof(request.CallId));

        var session = RequireParticipant(request.CallId);
        var updated = _sessions.Update(session.CallId, s =>
        {
            if (s.State == CallState.Declined)
                return;

            EnsureState(s, CallState.Pending, CallState.Ringing, CallState.Accepted, CallState.Connecting);
            s.State = CallState.Declined;
            s.EndReason = string.IsNullOrWhiteSpace(request.Reason) ? "declined" : request.Reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
        });

        await _history.TryPersistTerminalEventAsync(updated);
        var changed = ToStateChanged(updated, CallState.Declined, updated.EndReason);
        _audit.Info(updated, "call.declined", updated.EndReason);
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString())
            .SendAsync("call.declined", changed);
        _sessions.TryRemove(updated.CallId, out _);
    }

    public async Task Busy(BusyCallRequest request)
    {
        RequireCallId(request.CallId, nameof(request.CallId));

        var me = MeId;
        var session = RequireParticipant(request.CallId);
        if (session.CalleeUserId != me)
            throw new HubException("Only callee can report busy for this call.");

        var updated = _sessions.Update(session.CallId, s =>
        {
            if (s.State == CallState.Busy)
                return;

            EnsureState(s, CallState.Pending, CallState.Ringing);
            s.State = CallState.Busy;
            s.EndReason = string.IsNullOrWhiteSpace(request.Reason) ? "busy" : request.Reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
        });

        await _history.TryPersistTerminalEventAsync(updated);
        var changed = ToStateChanged(updated, CallState.Busy, updated.EndReason);
        _audit.Warn(updated, "call.busy", updated.EndReason);
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString())
            .SendAsync("call.busy", changed);
        _sessions.TryRemove(updated.CallId, out _);
    }

    public async Task Hangup(HangupCallRequest request)
    {
        RequireCallId(request.CallId, nameof(request.CallId));

        var me = MeId;
        var session = RequireParticipant(request.CallId);
        var updated = _sessions.Update(session.CallId, s =>
        {
            if (s.State is CallState.Cancelled or CallState.Ended)
                return;

            EnsureState(s, CallState.Pending, CallState.Ringing, CallState.Accepted, CallState.Connecting, CallState.Connected);
            var normalizedReason = string.IsNullOrWhiteSpace(request.Reason) ? "hangup" : request.Reason!;
            s.State = normalizedReason.Contains("cancel", StringComparison.OrdinalIgnoreCase) && !s.ConnectedAtUtc.HasValue
                ? CallState.Cancelled
                : CallState.Ended;
            s.EndReason = normalizedReason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me) s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else if (s.CalleeUserId == me) s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });

        _sessions.TouchUser(me);

        await _history.TryPersistTerminalEventAsync(updated);
        var eventName = updated.State == CallState.Cancelled ? "call.cancelled" : "call.ended";
        var changed = ToStateChanged(updated, updated.State, updated.EndReason);
        _audit.Info(updated, eventName, updated.EndReason);
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString())
            .SendAsync(eventName, changed);
        _sessions.TryRemove(updated.CallId, out _);
    }

    public async Task SendOffer(WebRtcOfferDto request)
    {
        RequireCallId(request.CallId, nameof(request.CallId));
        if (string.IsNullOrWhiteSpace(request.Sdp))
            throw new HubException("Invalid or missing 'sdp'.");

        var me = MeId;
        var session = RequireParticipant(request.CallId);
        var peer = _sessions.GetPeerId(session, me);
        var updated = _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State)) return;
            if (s.State is CallState.Accepted or CallState.Pending or CallState.Ringing)
                s.State = CallState.Connecting;
            s.LastOfferAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me) s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });

        _sessions.TouchUser(me);
        _audit.Info(updated, "call.offer", extra: new { fromUserId = me, sdpLength = request.Sdp.Length });
        await Clients.User(peer.ToString()).SendAsync("call.offer", new
        {
            request.CallId,
            request.Type,
            request.Sdp,
            fromUserId = me,
            correlationId = updated.CorrelationId
        });
    }

    public async Task SendAnswer(WebRtcAnswerDto request)
    {
        RequireCallId(request.CallId, nameof(request.CallId));
        if (string.IsNullOrWhiteSpace(request.Sdp))
            throw new HubException("Invalid or missing 'sdp'.");

        var me = MeId;
        var session = RequireParticipant(request.CallId);
        var peer = _sessions.GetPeerId(session, me);
        var updated = _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State)) return;
            if (s.State is CallState.Accepted or CallState.Pending or CallState.Ringing)
                s.State = CallState.Connecting;
            s.LastAnswerAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me) s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });

        _sessions.TouchUser(me);
        _audit.Info(updated, "call.answer", extra: new { fromUserId = me, sdpLength = request.Sdp.Length });
        await Clients.User(peer.ToString()).SendAsync("call.answer", new
        {
            request.CallId,
            request.Type,
            request.Sdp,
            fromUserId = me,
            correlationId = updated.CorrelationId
        });
    }

    public async Task SendIceCandidate(IceCandidateDto request)
    {
        RequireCallId(request.CallId, nameof(request.CallId));

        var me = MeId;
        var session = RequireParticipant(request.CallId);
        var peer = _sessions.GetPeerId(session, me);
        var updated = _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State)) return;
            if (s.State is CallState.Accepted or CallState.Pending or CallState.Ringing)
                s.State = CallState.Connecting;
            s.LastIceCandidateAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me) s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });

        _sessions.TouchUser(me);
        _audit.Info(updated, "call.ice-candidate", extra: new
        {
            fromUserId = me,
            request.SdpMid,
            request.SdpMLineIndex,
            hasCandidate = !string.IsNullOrWhiteSpace(request.Candidate)
        });
        await Clients.User(peer.ToString()).SendAsync("call.ice-candidate", new
        {
            request.CallId,
            request.Candidate,
            request.SdpMid,
            request.SdpMLineIndex,
            request.UsernameFragment,
            fromUserId = me,
            correlationId = updated.CorrelationId
        });
    }

    public async Task MarkConnected(MarkConnectedRequest request)
    {
        RequireCallId(request.CallId, nameof(request.CallId));

        var me = MeId;
        var session = RequireParticipant(request.CallId);
        var updated = _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State)) return;
            s.State = CallState.Connected;
            s.ConnectedAtUtc ??= DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me) s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });

        _sessions.TouchUser(me);
        var changed = ToStateChanged(updated, CallState.Connected, null);
        _audit.Info(updated, "call.connected");
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString())
            .SendAsync("call.connected", changed);
    }

    public async Task ReportFailure(ReportFailureRequest request)
    {
        RequireCallId(request.CallId, nameof(request.CallId));

        var me = MeId;
        var session = RequireParticipant(request.CallId);
        var updated = _sessions.Update(session.CallId, s =>
        {
            if (s.State == CallState.Failed)
                return;

            EnsureState(s, CallState.Pending, CallState.Ringing, CallState.Accepted, CallState.Connecting, CallState.Connected);
            s.State = CallState.Failed;
            s.EndReason = string.IsNullOrWhiteSpace(request.Reason) ? "failed" : request.Reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me) s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });

        _sessions.TouchUser(me);

        await _history.TryPersistTerminalEventAsync(updated);
        var changed = ToStateChanged(updated, CallState.Failed, updated.EndReason);
        _audit.Warn(updated, "call.failed", updated.EndReason);
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString())
            .SendAsync("call.failed", changed);
        _sessions.TryRemove(updated.CallId, out _);
    }

    public Task HeartbeatCall(HeartbeatCallRequest request)
    {
        RequireCallId(request.CallId, nameof(request.CallId));

        var session = RequireParticipant(request.CallId);
        var me = MeId;
        _sessions.TouchCallParticipant(request.CallId, me);
        _audit.Info(session, "call.heartbeat", extra: new { connectionId = Context.ConnectionId, userId = me });
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

    private static void RequireCallId(Guid callId, string propertyName)
    {
        if (callId == Guid.Empty)
            throw new HubException($"Invalid or missing '{propertyName}'.");
    }

    private static void EnsureState(CallSession session, params CallState[] allowedStates)
    {
        if (allowedStates.Contains(session.State))
            return;
        throw new HubException($"Invalid call state transition from '{session.State}'.");
    }

    private static CallStateChangedDto ToStateChanged(CallSession session, CallState state, string? reason)
        => new()
        {
            CallId = session.CallId,
            CallerUserId = session.CallerUserId,
            CalleeUserId = session.CalleeUserId,
            DialogId = session.DialogId,
            Type = session.Type,
            State = state,
            OccurredAtUtc = DateTime.UtcNow,
            Reason = reason,
            CorrelationId = session.CorrelationId
        };
}
