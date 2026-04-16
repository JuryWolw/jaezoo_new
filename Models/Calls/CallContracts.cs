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

public sealed class IceServerDto
{
    public string[] Urls { get; set; } = Array.Empty<string>();
    public string Username { get; set; } = string.Empty;
    public string Credential { get; set; } = string.Empty;
    public string CredentialType { get; set; } = "password";

    public IceServerDto() { }
    public IceServerDto(string[] urls, string username, string credential, string credentialType = "password")
    {
        Urls = urls ?? Array.Empty<string>();
        Username = username ?? string.Empty;
        Credential = credential ?? string.Empty;
        CredentialType = string.IsNullOrWhiteSpace(credentialType) ? "password" : credentialType;
    }
}

public sealed class IceConfigResponse
{
    public IReadOnlyList<IceServerDto> IceServers { get; set; } = Array.Empty<IceServerDto>();
    public int TtlSeconds { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    public IceConfigResponse() { }
    public IceConfigResponse(IReadOnlyList<IceServerDto> iceServers, int ttlSeconds, DateTime expiresAtUtc)
    {
        IceServers = iceServers ?? Array.Empty<IceServerDto>();
        TtlSeconds = ttlSeconds;
        ExpiresAtUtc = expiresAtUtc;
    }
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

public sealed class WebRtcOfferDto
{
    public Guid CallId { get; set; }
    [Required] public string Sdp { get; set; } = string.Empty;
    public string Type { get; set; } = "offer";

    public WebRtcOfferDto() { }
    public WebRtcOfferDto(Guid callId, string sdp, string type = "offer")
    {
        CallId = callId;
        Sdp = sdp ?? string.Empty;
        Type = string.IsNullOrWhiteSpace(type) ? "offer" : type;
    }
}

public sealed class WebRtcAnswerDto
{
    public Guid CallId { get; set; }
    [Required] public string Sdp { get; set; } = string.Empty;
    public string Type { get; set; } = "answer";

    public WebRtcAnswerDto() { }
    public WebRtcAnswerDto(Guid callId, string sdp, string type = "answer")
    {
        CallId = callId;
        Sdp = sdp ?? string.Empty;
        Type = string.IsNullOrWhiteSpace(type) ? "answer" : type;
    }
}

public sealed class IceCandidateDto
{
    public Guid CallId { get; set; }
    public string Candidate { get; set; } = string.Empty;
    public string? SdpMid { get; set; }
    public int? SdpMLineIndex { get; set; }
    public string? UsernameFragment { get; set; }

    public IceCandidateDto() { }
    public IceCandidateDto(Guid callId, string candidate, string? sdpMid, int? sdpMLineIndex, string? usernameFragment = null)
    {
        CallId = callId;
        Candidate = candidate ?? string.Empty;
        SdpMid = sdpMid;
        SdpMLineIndex = sdpMLineIndex;
        UsernameFragment = usernameFragment;
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
    public DateTime? LastOfferAtUtc { get; set; }
    public DateTime? LastAnswerAtUtc { get; set; }
    public DateTime? LastIceCandidateAtUtc { get; set; }
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
