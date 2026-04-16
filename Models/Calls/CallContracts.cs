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
}

public sealed class IceConfigResponse
{
    public IReadOnlyList<IceServerDto> IceServers { get; set; } = Array.Empty<IceServerDto>();
    public int TtlSeconds { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class StartCallRequest
{
    public Guid PeerUserId { get; set; }
    public Guid? DialogId { get; set; }
    public CallType Type { get; set; } = CallType.Voice;
    public string? ClientVersion { get; set; }
    public string? DeviceInfo { get; set; }
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
}

public sealed class AcceptCallRequest
{
    public Guid CallId { get; set; }
    public string? ClientVersion { get; set; }
    public string? DeviceInfo { get; set; }
}

public sealed class DeclineCallRequest
{
    public Guid CallId { get; set; }
    public string? Reason { get; set; }
}

public sealed class HangupCallRequest
{
    public Guid CallId { get; set; }
    public string? Reason { get; set; }
}

public sealed class BusyCallRequest
{
    public Guid CallId { get; set; }
    public string? Reason { get; set; }
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
}

public sealed class WebRtcOfferDto
{
    public Guid CallId { get; set; }
    [Required]
    public string Sdp { get; set; } = string.Empty;
    public string Type { get; set; } = "offer";
}

public sealed class WebRtcAnswerDto
{
    public Guid CallId { get; set; }
    [Required]
    public string Sdp { get; set; } = string.Empty;
    public string Type { get; set; } = "answer";
}

public sealed class IceCandidateDto
{
    public Guid CallId { get; set; }
    public string Candidate { get; set; } = string.Empty;
    public string? SdpMid { get; set; }
    public int? SdpMLineIndex { get; set; }
    public string? UsernameFragment { get; set; }
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
}

public sealed class ReportFailureRequest
{
    public Guid CallId { get; set; }
    public string? Reason { get; set; }
}

public sealed class HeartbeatCallRequest
{
    public Guid CallId { get; set; }
}
