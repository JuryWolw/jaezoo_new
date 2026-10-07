using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models.Calls;

public enum CallType
{
    Voice = 0,
    Video = 1,
    ScreenShare = 2
}

public enum CallState
{
    Pending = 0,
    Ringing = 1,
    Accepted = 2,
    Connecting = 3,
    Connected = 4,
    Declined = 5,
    Busy = 6,
    Missed = 7,
    Ended = 8,
    Failed = 9,
    Cancelled = 10,
    TimedOut = 11
}

public sealed class StartCallRequest
{
    public Guid PeerUserId { get; set; }
    public Guid? DialogId { get; set; }
    public CallType Type { get; set; } = CallType.Voice;
    public string? ClientVersion { get; set; }
    public string? DeviceInfo { get; set; }

    public StartCallRequest() { }
    public StartCallRequest(Guid peerUserId, Guid? dialogId, CallType type = CallType.Voice, string? clientVersion = null, string? deviceInfo = null)
    {
        PeerUserId = peerUserId;
        DialogId = dialogId;
        Type = type;
        ClientVersion = clientVersion;
        DeviceInfo = deviceInfo;
    }
}

public sealed class StartCallResponse
{
    public Guid CallId { get; set; }
    public Guid PeerUserId { get; set; }
    public Guid? DialogId { get; set; }
    public CallType Type { get; set; }
    public CallState State { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CorrelationId { get; set; } = string.Empty;

    public StartCallResponse() { }
    public StartCallResponse(Guid callId, Guid peerUserId, Guid? dialogId, CallType type, CallState state, DateTime createdAtUtc, string correlationId)
    {
        CallId = callId;
        PeerUserId = peerUserId;
        DialogId = dialogId;
        Type = type;
        State = state;
        CreatedAtUtc = createdAtUtc;
        CorrelationId = correlationId ?? string.Empty;
    }
}

public sealed class CallInviteDto
{
    public Guid CallId { get; set; }
    public Guid CallerUserId { get; set; }
    public Guid CalleeUserId { get; set; }
    public Guid? DialogId { get; set; }
    public CallType Type { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string? CallerDisplayName { get; set; }
    public string? CallerAvatarUrl { get; set; }

    public CallInviteDto() { }
    public CallInviteDto(Guid callId, Guid callerUserId, Guid calleeUserId, Guid? dialogId, CallType type, DateTime createdAtUtc, string correlationId, string? callerDisplayName, string? callerAvatarUrl)
    {
        CallId = callId;
        CallerUserId = callerUserId;
        CalleeUserId = calleeUserId;
        DialogId = dialogId;
        Type = type;
        CreatedAtUtc = createdAtUtc;
        CorrelationId = correlationId ?? string.Empty;
        CallerDisplayName = callerDisplayName;
        CallerAvatarUrl = callerAvatarUrl;
    }
}

public sealed class AcceptCallRequest
{
    public Guid CallId { get; set; }
    public string? ClientVersion { get; set; }
    public string? DeviceInfo { get; set; }

    public AcceptCallRequest() { }
    public AcceptCallRequest(Guid callId, string? clientVersion = null, string? deviceInfo = null)
    {
        CallId = callId;
        ClientVersion = clientVersion;
        DeviceInfo = deviceInfo;
    }
}

public sealed class DeclineCallRequest
{
    public Guid CallId { get; set; }
    public string? Reason { get; set; }

    public DeclineCallRequest() { }
    public DeclineCallRequest(Guid callId, string? reason = null)
    {
        CallId = callId;
        Reason = reason;
    }
}

public sealed class HangupCallRequest
{
    public Guid CallId { get; set; }
    public string? Reason { get; set; }

    public HangupCallRequest() { }
    public HangupCallRequest(Guid callId, string? reason = null)
    {
        CallId = callId;
        Reason = reason;
    }
}

public sealed class BusyCallRequest
{
    public Guid CallId { get; set; }
    public string? Reason { get; set; }

    public BusyCallRequest() { }
    public BusyCallRequest(Guid callId, string? reason = null)
    {
        CallId = callId;
        Reason = reason;
    }
}

public sealed class CallStateChangedDto
{
    public Guid CallId { get; set; }
    public Guid CallerUserId { get; set; }
    public Guid CalleeUserId { get; set; }
    public Guid? DialogId { get; set; }
    public CallType Type { get; set; }
    public CallState State { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string? Reason { get; set; }
    public string CorrelationId { get; set; } = string.Empty;

    public CallStateChangedDto() { }
    public CallStateChangedDto(Guid callId, Guid callerUserId, Guid calleeUserId, Guid? dialogId, CallType type, CallState state, DateTime occurredAtUtc, string? reason, string correlationId)
    {
        CallId = callId;
        CallerUserId = callerUserId;
        CalleeUserId = calleeUserId;
        DialogId = dialogId;
        Type = type;
        State = state;
        OccurredAtUtc = occurredAtUtc;
        Reason = reason;
        CorrelationId = correlationId ?? string.Empty;
    }
}

public sealed class CallSession
{
    public Guid CallId { get; init; }
    public Guid CallerUserId { get; init; }
    public Guid CalleeUserId { get; init; }
    public Guid? DialogId { get; init; }
    public CallType Type { get; init; }
    public CallState State { get; set; } = CallState.Pending;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? ConnectedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime? LastActivityAtUtc { get; set; }
    public DateTime? LastCallerActivityAtUtc { get; set; }
    public DateTime? LastCalleeActivityAtUtc { get; set; }
    public string? EndReason { get; set; }
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
    public string? CallerClientVersion { get; set; }
    public string? CallerDeviceInfo { get; set; }
    public string? CalleeClientVersion { get; set; }
    public string? CalleeDeviceInfo { get; set; }
    public DateTime? HistoryPersistedAtUtc { get; set; }
}

public sealed class MarkConnectedRequest
{
    public Guid CallId { get; set; }
    public MarkConnectedRequest() { }
    public MarkConnectedRequest(Guid callId) => CallId = callId;
}

public sealed class ReportFailureRequest
{
    public Guid CallId { get; set; }
    public string? Reason { get; set; }
    public ReportFailureRequest() { }
    public ReportFailureRequest(Guid callId, string? reason = null)
    {
        CallId = callId;
        Reason = reason;
    }
}

public sealed class HeartbeatCallRequest
{
    public Guid CallId { get; set; }
    public HeartbeatCallRequest() { }
    public HeartbeatCallRequest(Guid callId) => CallId = callId;
}

public sealed class CallJoinResponse
{
    public Guid CallId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}
