using System.Security.Claims;
using System.Text.Json;
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

    public async Task AcceptCall(object? request)
    {
        var parsed = ParseAcceptCallRequest(request);
        var me = MeId;
        var session = RequireParticipant(parsed.CallId);

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
            s.CalleeClientVersion = parsed.ClientVersion;
            s.CalleeDeviceInfo = parsed.DeviceInfo;
        });
        _sessions.TouchUser(me);

        var changed = ToStateChanged(updated, updated.State, null);
        _audit.Info(updated, "call.accepted");
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString()).SendAsync("call.accepted", changed);
    }

    public async Task DeclineCall(object? request)
    {
        var parsed = ParseDeclineCallRequest(request);
        var session = RequireParticipant(parsed.CallId);
        var updated = _sessions.Update(session.CallId, s =>
        {
            if (s.State == CallState.Declined)
                return;
            EnsureState(s, CallState.Pending, CallState.Ringing, CallState.Accepted, CallState.Connecting);
            s.State = CallState.Declined;
            s.EndReason = string.IsNullOrWhiteSpace(parsed.Reason) ? "declined" : parsed.Reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
        });

        await _history.TryPersistTerminalEventAsync(updated);
        var changed = ToStateChanged(updated, CallState.Declined, updated.EndReason);
        _audit.Info(updated, "call.declined", updated.EndReason);
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString()).SendAsync("call.declined", changed);
        _sessions.TryRemove(updated.CallId, out _);
    }

    public async Task Busy(object? request)
    {
        var parsed = ParseBusyCallRequest(request);
        var me = MeId;
        var session = RequireParticipant(parsed.CallId);
        if (session.CalleeUserId != me)
            throw new HubException("Only callee can report busy for this call.");

        var updated = _sessions.Update(session.CallId, s =>
        {
            if (s.State == CallState.Busy)
                return;
            EnsureState(s, CallState.Pending, CallState.Ringing);
            s.State = CallState.Busy;
            s.EndReason = string.IsNullOrWhiteSpace(parsed.Reason) ? "busy" : parsed.Reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
        });

        await _history.TryPersistTerminalEventAsync(updated);
        var changed = ToStateChanged(updated, CallState.Busy, updated.EndReason);
        _audit.Warn(updated, "call.busy", updated.EndReason);
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString()).SendAsync("call.busy", changed);
        _sessions.TryRemove(updated.CallId, out _);
    }

    public async Task Hangup(object? request)
    {
        var parsed = ParseHangupCallRequest(request);
        var me = MeId;
        var session = RequireParticipant(parsed.CallId);
        var updated = _sessions.Update(session.CallId, s =>
        {
            if (s.State is CallState.Cancelled or CallState.Ended)
                return;
            EnsureState(s, CallState.Pending, CallState.Ringing, CallState.Accepted, CallState.Connecting, CallState.Connected);
            var normalizedReason = string.IsNullOrWhiteSpace(parsed.Reason) ? "hangup" : parsed.Reason!;
            s.State = normalizedReason.Contains("cancel", StringComparison.OrdinalIgnoreCase) && !s.ConnectedAtUtc.HasValue ? CallState.Cancelled : CallState.Ended;
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
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString()).SendAsync(eventName, changed);
        _sessions.TryRemove(updated.CallId, out _);
    }

    public async Task SendOffer(object? request)
    {
        var parsed = ParseWebRtcOfferDto(request);
        var me = MeId;
        var session = RequireParticipant(parsed.CallId);
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
        _audit.Info(updated, "call.offer", extra: new { fromUserId = me, sdpLength = parsed.Sdp?.Length ?? 0 });
        await Clients.User(peer.ToString()).SendAsync("call.offer", new { parsed.CallId, parsed.Type, parsed.Sdp, fromUserId = me, correlationId = updated.CorrelationId });
    }

    public async Task SendAnswer(object? request)
    {
        var parsed = ParseWebRtcAnswerDto(request);
        var me = MeId;
        var session = RequireParticipant(parsed.CallId);
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
        _audit.Info(updated, "call.answer", extra: new { fromUserId = me, sdpLength = parsed.Sdp?.Length ?? 0 });
        await Clients.User(peer.ToString()).SendAsync("call.answer", new { parsed.CallId, parsed.Type, parsed.Sdp, fromUserId = me, correlationId = updated.CorrelationId });
    }

    public async Task SendIceCandidate(object? request)
    {
        var parsed = ParseIceCandidateDto(request);
        var me = MeId;
        var session = RequireParticipant(parsed.CallId);
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
        _audit.Info(updated, "call.ice-candidate", extra: new { fromUserId = me, parsed.SdpMid, parsed.SdpMLineIndex, hasCandidate = !string.IsNullOrWhiteSpace(parsed.Candidate) });
        await Clients.User(peer.ToString()).SendAsync("call.ice-candidate", new { parsed.CallId, parsed.Candidate, parsed.SdpMid, parsed.SdpMLineIndex, parsed.UsernameFragment, fromUserId = me, correlationId = updated.CorrelationId });
    }

    public async Task MarkConnected(object? request)
    {
        var parsed = ParseMarkConnectedRequest(request);
        var me = MeId;
        var session = RequireParticipant(parsed.CallId);
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
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString()).SendAsync("call.connected", changed);
    }

    public async Task ReportFailure(object? request)
    {
        var parsed = ParseReportFailureRequest(request);
        var me = MeId;
        var session = RequireParticipant(parsed.CallId);
        var updated = _sessions.Update(session.CallId, s =>
        {
            if (s.State == CallState.Failed)
                return;
            EnsureState(s, CallState.Pending, CallState.Ringing, CallState.Accepted, CallState.Connecting, CallState.Connected);
            s.State = CallState.Failed;
            s.EndReason = string.IsNullOrWhiteSpace(parsed.Reason) ? "failed" : parsed.Reason;
            s.EndedAtUtc = DateTime.UtcNow;
            s.LastActivityAtUtc = DateTime.UtcNow;
            if (s.CallerUserId == me) s.LastCallerActivityAtUtc = DateTime.UtcNow;
            else s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });
        _sessions.TouchUser(me);

        await _history.TryPersistTerminalEventAsync(updated);
        var changed = ToStateChanged(updated, CallState.Failed, updated.EndReason);
        _audit.Warn(updated, "call.failed", updated.EndReason);
        await Clients.Users(updated.CallerUserId.ToString(), updated.CalleeUserId.ToString()).SendAsync("call.failed", changed);
        _sessions.TryRemove(updated.CallId, out _);
    }

    public Task HeartbeatCall(object? request)
    {
        var parsed = ParseHeartbeatCallRequest(request);
        var session = RequireParticipant(parsed.CallId);
        var me = MeId;
        _sessions.TouchCallParticipant(parsed.CallId, me);
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

    private static AcceptCallRequest ParseAcceptCallRequest(object? payload)
        => new()
        {
            CallId = ReadRequiredGuid(payload, "callId"),
            ClientVersion = ReadOptionalString(payload, "clientVersion"),
            DeviceInfo = ReadOptionalString(payload, "deviceInfo")
        };

    private static DeclineCallRequest ParseDeclineCallRequest(object? payload)
        => new()
        {
            CallId = ReadRequiredGuid(payload, "callId"),
            Reason = ReadOptionalString(payload, "reason")
        };

    private static BusyCallRequest ParseBusyCallRequest(object? payload)
        => new()
        {
            CallId = ReadRequiredGuid(payload, "callId"),
            Reason = ReadOptionalString(payload, "reason")
        };

    private static HangupCallRequest ParseHangupCallRequest(object? payload)
        => new()
        {
            CallId = ReadRequiredGuid(payload, "callId"),
            Reason = ReadOptionalString(payload, "reason")
        };

    private static WebRtcOfferDto ParseWebRtcOfferDto(object? payload)
        => new()
        {
            CallId = ReadRequiredGuid(payload, "callId"),
            Sdp = ReadRequiredString(payload, "sdp"),
            Type = ReadOptionalString(payload, "type") ?? "offer"
        };

    private static WebRtcAnswerDto ParseWebRtcAnswerDto(object? payload)
        => new()
        {
            CallId = ReadRequiredGuid(payload, "callId"),
            Sdp = ReadRequiredString(payload, "sdp"),
            Type = ReadOptionalString(payload, "type") ?? "answer"
        };

    private static IceCandidateDto ParseIceCandidateDto(object? payload)
        => new()
        {
            CallId = ReadRequiredGuid(payload, "callId"),
            Candidate = ReadOptionalString(payload, "candidate") ?? string.Empty,
            SdpMid = ReadOptionalString(payload, "sdpMid"),
            SdpMLineIndex = ReadOptionalInt(payload, "sdpMLineIndex"),
            UsernameFragment = ReadOptionalString(payload, "usernameFragment")
        };

    private static MarkConnectedRequest ParseMarkConnectedRequest(object? payload)
        => new() { CallId = ReadRequiredGuid(payload, "callId") };

    private static ReportFailureRequest ParseReportFailureRequest(object? payload)
        => new()
        {
            CallId = ReadRequiredGuid(payload, "callId"),
            Reason = ReadOptionalString(payload, "reason")
        };

    private static HeartbeatCallRequest ParseHeartbeatCallRequest(object? payload)
        => new() { CallId = ReadRequiredGuid(payload, "callId") };

    private static Guid ReadRequiredGuid(object? payload, string propertyName)
    {
        var element = ToJsonElement(payload);

        if (element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var directGuid))
            return directGuid;

        if (element.ValueKind == JsonValueKind.Object && TryGetPropertyCaseInsensitive(element, propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.String && Guid.TryParse(property.GetString(), out var propertyGuid))
                return propertyGuid;
        }

        throw new HubException($"Invalid or missing '{propertyName}'.");
    }

    private static string ReadRequiredString(object? payload, string propertyName)
    {
        var value = ReadOptionalString(payload, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        throw new HubException($"Invalid or missing '{propertyName}'.");
    }

    private static string? ReadOptionalString(object? payload, string propertyName)
    {
        var element = ToJsonElement(payload);

        if (element.ValueKind == JsonValueKind.String && string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase))
            return element.GetString();

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return TryGetPropertyCaseInsensitive(element, propertyName, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.ToString()
            }
            : null;
    }

    private static int? ReadOptionalInt(object? payload, string propertyName)
    {
        var element = ToJsonElement(payload);
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetPropertyCaseInsensitive(element, propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
            return value;

        return null;
    }

    private static JsonElement ToJsonElement(object? payload)
    {
        if (payload is null)
            return default;
        if (payload is JsonElement element)
            return element;
        return JsonSerializer.SerializeToElement(payload, payload.GetType());
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
