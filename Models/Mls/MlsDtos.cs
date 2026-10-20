namespace JaeZoo.Server.Models.Mls;

public sealed record RegisterDeviceRequest(Guid DeviceId, string? Name, string? Platform, string? ClientVersion, byte[] SignaturePublicKey);

public sealed record RenameDeviceRequest(string Name);

/// <summary>"Мои устройства": one row per device, merged with its login session.</summary>
public sealed record UserDeviceDto(
    Guid Id,
    string Name,
    string Platform,
    string ClientVersion,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    bool IsCurrent,
    DateTime? RevokedAt,
    string? RevokeReason);

public sealed record DeviceRefDto(Guid UserId, Guid DeviceId);

public sealed record UploadKeyPackagesRequest(Guid DeviceId, IReadOnlyList<byte[]>? KeyPackages, byte[]? LastResort);

public sealed record KeyPackageStatusDto(int OneTime, bool HasLastResort, int Target);

public sealed record ClaimKeyPackagesRequest(IReadOnlyList<Guid> DeviceIds);

public sealed record ClaimedKeyPackageDto(Guid UserId, Guid DeviceId, byte[] KeyPackage, bool IsLastResort);

/// <summary>
/// MLS view of a conversation: the group (if created), its epoch, the devices the server
/// currently has as leaves, and the active devices of all participants (what the roster should be).
/// </summary>
public sealed record MlsConversationDto(
    MlsConversationKind Kind,
    Guid ConversationId,
    Guid? GroupId,
    long Epoch,
    IReadOnlyList<DeviceRefDto> Members,
    IReadOnlyList<DeviceRefDto> ExpectedDevices,
    long? GroupInfoEpoch = null);

public sealed record CreateMlsGroupRequest(Guid GroupId, Guid DeviceId, MlsConversationKind Kind, Guid ConversationId);

/// <summary>
/// A commit built on <paramref name="Epoch"/>. <paramref name="Added"/>/<paramref name="Removed"/> declare the
/// roster change (the server cannot parse MLS; receivers verify the real effect cryptographically).
/// <paramref name="Welcome"/> is one MLS Welcome for all added devices.
/// </summary>
public sealed record SubmitCommitRequest(
    long Epoch,
    Guid DeviceId,
    byte[] Commit,
    IReadOnlyList<DeviceRefDto>? Added,
    IReadOnlyList<DeviceRefDto>? Removed,
    byte[]? Welcome);

public sealed record CommitAcceptedDto(long EventId, long Epoch);

public sealed record MlsEventDto(
    long Id,
    Guid GroupId,
    MlsEventKind Kind,
    long Epoch,
    Guid SenderUserId,
    Guid? SenderDeviceId,
    DateTime CreatedAt,
    byte[]? Commit,
    MessageDto? Message);

public sealed record MlsWelcomeDto(
    Guid Id,
    Guid GroupId,
    long Epoch,
    long CommitEventId,
    MlsConversationKind Kind,
    Guid ConversationId,
    byte[] Welcome);

public sealed record MlsDeviceRevokedDto(Guid UserId, Guid DeviceId);

/// <summary>GroupInfo of <paramref name="Epoch"/>, published by a member device after its commit (or to heal a stale one).</summary>
public sealed record PublishGroupInfoRequest(Guid DeviceId, long Epoch, byte[] GroupInfo);

public sealed record MlsGroupInfoDto(Guid GroupId, long Epoch, byte[] GroupInfo);

/// <summary>External Commit (RFC 9420 §12.4.3.2) by <paramref name="DeviceId"/>, built on <paramref name="Epoch"/>.</summary>
public sealed record ExternalJoinRequest(Guid DeviceId, long Epoch, byte[] Commit);

/// <summary>A conversation whose MLS group this device can join by External Commit (it is not a leaf yet).</summary>
public sealed record MissingConversationDto(MlsConversationKind Kind, Guid ConversationId, Guid GroupId);
