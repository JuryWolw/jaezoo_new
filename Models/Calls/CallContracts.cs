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

public record IceServerDto(
    string[] Urls,
    string Username,
    string Credential,
    string CredentialType = "password"
);

public record IceConfigResponse(
    IReadOnlyList<IceServerDto> IceServers,
    int TtlSeconds,
    DateTime ExpiresAtUtc
);

public record StartCallRequest(
    Guid PeerUserId,
    Guid? DialogId,
    CallType Type = CallType.Voice,
    string? ClientVersion = null,
    string? DeviceInfo = null
);

public record StartCallResponse(
    Guid CallId,
    Guid PeerUserId,
    Guid? DialogId,
    CallType Type,
    CallState State,
    DateTime CreatedAtUtc,
    string CorrelationId
);

public record CallInviteDto(
    Guid CallId,
    Guid CallerUserId,
    Guid CalleeUserId,
    Guid? DialogId,
    CallType Type,
    DateTime CreatedAtUtc,
    string CorrelationId,
    string? CallerDisplayName,
    string? CallerAvatarUrl
);

public record AcceptCallRequest(
    Guid CallId,
    string? ClientVersion = null,
    string? DeviceInfo = null
);

public record DeclineCallRequest(
    Guid CallId,
    string? Reason = null
);

public record HangupCallRequest(
    Guid CallId,
    string? Reason = null
);

public record BusyCallRequest(
    Guid CallId,
    string? Reason = null
);

public record CallStateChangedDto(
    Guid CallId,
    Guid CallerUserId,
    Guid CalleeUserId,
    Guid? DialogId,
    CallType Type,
    CallState State,
    DateTime OccurredAtUtc,
    string? Reason,
    string CorrelationId
);

public record WebRtcOfferDto(
    Guid CallId,
    [property: Required] string Sdp,
    string Type = "offer"
);

public record WebRtcAnswerDto(
    Guid CallId,
    [property: Required] string Sdp,
    string Type = "answer"
);

public record IceCandidateDto(
    Guid CallId,
    string Candidate,
    string? SdpMid,
    int? SdpMLineIndex,
    string? UsernameFragment = null
);

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
    public string? EndReason { get; set; }
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
    public string? CallerClientVersion { get; set; }
    public string? CallerDeviceInfo { get; set; }
    public string? CalleeClientVersion { get; set; }
    public string? CalleeDeviceInfo { get; set; }
}
