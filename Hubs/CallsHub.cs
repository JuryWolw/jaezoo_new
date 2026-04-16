using System.Security.Claims;
using JaeZoo.Server.Models.Calls;
using JaeZoo.Server.Services.Calls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace JaeZoo.Server.Hubs;

[Authorize]
public sealed class CallsHub : Hub
{
    private const string VersionMarker = "CALLS_ALIGN_V4_20260416";

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
                throw new HubException($"{VersionMarker}|No user id claim.");

            return id;
        }
    }

    public override Task OnConnectedAsync()
    {
        _sessions.MarkUserConnected(MeId, Context.ConnectionId);
        _logger.LogInformation("CallsHub[{Version}] connected. UserId={UserId} ConnectionId={ConnectionId}", VersionMarker, MeId, Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _sessions.MarkUserDisconnected(MeId, Context.ConnectionId);
        _logger.LogInformation(exception, "CallsHub[{Version}] disconnected. UserId={UserId} ConnectionId={ConnectionId}", VersionMarker, MeId, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task<IceConfigResponse> GetIceConfig() => Task.FromResult(_turn.CreateForUser(MeId));

    public async Task AcceptCall(AcceptCallRequest request)
    {
        var session = RequireParticipant(request.CallId);
        var me = MeId;

        if (session.CalleeUserId != me)
            throw new HubException($"{VersionMarker}|Only callee can accept the call.");

        var broadcast = false;
        _sessions.Update(session.CallId, s =>
        {
            if (s.State is CallState.Accepted or CallState.Connecting or CallState.Connected)
                return;

            EnsureState(s, CallState.Pending, CallState.Ringing);
            s.State = CallState.Accepted;
            s.AcceptedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            s.LastCalleeActivityAtUtc = DateTime.UtcNow;
            s.CalleeClientVersion = request.ClientVersion;
            s.CalleeDeviceInfo = request.DeviceInfo;
            broadcast = true;
        });
        _sessions.TouchUser(me);

        if (!broadcast)
            return;

        var changed = ToStateChanged(session, CallState.Accepted, null);
        _audit.Info(session, "call.accepted");

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync("call.accepted", changed);
    }

    public async Task DeclineCall(DeclineCallRequest request)
    {
        var session = RequireParticipant(request.CallId);
        if (!TryApplyTerminal(session, CallState.Declined, string.IsNullOrWhiteSpace(request.Reason) ? "declined" : request.Reason, out var changed))
            return;

        await _history.TryPersistTerminalEventAsync(session);
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
            throw new HubException($"{VersionMarker}|Only callee can report busy for this call.");

        if (!TryApplyTerminal(session, CallState.Busy, string.IsNullOrWhiteSpace(request.Reason) ? "busy" : request.Reason, out var changed, CallState.Pending, CallState.Ringing))
            return;

        await _history.TryPersistTerminalEventAsync(session);
        _audit.Warn(session, "call.busy", session.EndReason);

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync("call.busy", changed);

        _sessions.TryRemove(session.CallId, out _);
    }

    public async Task Hangup(HangupCallRequest request)
    {
        var session = RequireParticipant(request.CallId);
        var me = MeId;
        var changed = default(CallStateChangedDto);
        var eventName = "call.ended";
        var shouldBroadcast = false;

        _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State))
                return;

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

            changed = ToStateChanged(s, s.State, s.EndReason);
            eventName = s.State == CallState.Cancelled ? "call.cancelled" : "call.ended";
            shouldBroadcast = true;
        });
        _sessions.TouchUser(me);

        if (!shouldBroadcast || changed is null)
            return;

        await _history.TryPersistTerminalEventAsync(session);
        _audit.Info(session, eventName, session.EndReason);

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync(eventName, changed);

        _sessions.TryRemove(session.CallId, out _);
    }

    public async Task SendOffer(WebRtcOfferDto request)
    {
        if (request.CallId == Guid.Empty || string.IsNullOrWhiteSpace(request.Sdp))
            throw new HubException($"{VersionMarker}|Invalid offer payload.");

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

        _audit.Info(session, "call.offer", extra: new { fromUserId = me, sdpLength = request.Sdp.Length, marker = VersionMarker });

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
        if (request.CallId == Guid.Empty || string.IsNullOrWhiteSpace(request.Sdp))
            throw new HubException($"{VersionMarker}|Invalid answer payload.");

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

        _audit.Info(session, "call.answer", extra: new { fromUserId = me, sdpLength = request.Sdp.Length, marker = VersionMarker });

        await Clients.User(peer.ToString()).SendAsync("call.answer", new
        {
            request.CallId,
            request.Type,
            request.Sdp,
            fromUserId = me,
            correlationId = session.CorrelationId
        });
    }

    public async Task SendIceCandidate(IceCandidateDto request)
    {
        if (request.CallId == Guid.Empty)
            throw new HubException($"{VersionMarker}|Invalid ICE payload: empty callId.");

        var session = RequireParticipant(request.CallId);
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

        _audit.Info(session, "call.ice-candidate", extra: new { fromUserId = me, request.SdpMid, request.SdpMLineIndex, hasCandidate = !string.IsNullOrWhiteSpace(request.Candidate), marker = VersionMarker });

        await Clients.User(peer.ToString()).SendAsync("call.ice-candidate", new
        {
            request.CallId,
            request.Candidate,
            request.SdpMid,
            request.SdpMLineIndex,
            request.UsernameFragment,
            fromUserId = me,
            correlationId = session.CorrelationId
        });
    }

    public async Task MarkConnected(MarkConnectedRequest request)
    {
        if (request.CallId == Guid.Empty)
            return;

        var session = RequireParticipant(request.CallId);
        var me = MeId;
        var broadcast = false;

        _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State))
                return;
            if (s.State == CallState.Connected)
                return;

            s.State = CallState.Connected;
            s.ConnectedAtUtc ??= DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me)
                s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else
                s.LastCalleeActivityAtUtc = DateTime.UtcNow;
            broadcast = true;
        });
        _sessions.TouchUser(me);

        if (!broadcast)
            return;

        var changed = ToStateChanged(session, CallState.Connected, null);
        _audit.Info(session, "call.connected", extra: new { marker = VersionMarker });

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync("call.connected", changed);
    }

    public async Task ReportFailure(ReportFailureRequest request)
    {
        if (request.CallId == Guid.Empty)
            return;

        var session = RequireParticipant(request.CallId);
        var me = MeId;
        var changed = default(CallStateChangedDto);
        var shouldBroadcast = false;

        _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State))
                return;

            s.State = CallState.Failed;
            s.EndReason = string.IsNullOrWhiteSpace(request.Reason) ? "failed" : request.Reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me)
                s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else
                s.LastCalleeActivityAtUtc = DateTime.UtcNow;
            changed = ToStateChanged(s, CallState.Failed, s.EndReason);
            shouldBroadcast = true;
        });
        _sessions.TouchUser(me);

        if (!shouldBroadcast || changed is null)
            return;

        await _history.TryPersistTerminalEventAsync(session);
        _audit.Warn(session, "call.failed", session.EndReason, new { marker = VersionMarker });

        await Clients.Users(session.CallerUserId.ToString(), session.CalleeUserId.ToString())
            .SendAsync("call.failed", changed);

        _sessions.TryRemove(session.CallId, out _);
    }

    public Task HeartbeatCall(HeartbeatCallRequest request)
    {
        if (request.CallId == Guid.Empty)
            return Task.CompletedTask;

        if (!_sessions.TryGet(request.CallId, out var session) || session is null)
            return Task.CompletedTask;

        if (!_sessions.IsParticipant(session, MeId))
            return Task.CompletedTask;

        _sessions.TouchCallParticipant(request.CallId, MeId);
        return Task.CompletedTask;
    }

    private bool TryApplyTerminal(CallSession session, CallState state, string reason, out CallStateChangedDto? changed, params CallState[] allowed)
    {
        changed = null;
        var changedFlag = false;
        _sessions.Update(session.CallId, s =>
        {
            if (!CallSessionService.IsActiveState(s.State))
                return;

            if (allowed.Length != 0)
                EnsureState(s, allowed);

            s.State = state;
            s.EndReason = reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            changed = ToStateChanged(s, state, s.EndReason);
            changedFlag = true;
        });

        return changedFlag && changed is not null;
    }

    private CallSession RequireParticipant(Guid callId)
    {
        if (!_sessions.TryGet(callId, out var session) || session is null)
            throw new HubException($"{VersionMarker}|Call session not found.|callId={callId}");

        if (!_sessions.IsParticipant(session, MeId))
            throw new HubException($"{VersionMarker}|User is not a participant of this call.|callId={callId}|userId={MeId}");

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
