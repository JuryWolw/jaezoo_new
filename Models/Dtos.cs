namespace JaeZoo.Server.Models;

public sealed class RegisterRequest
{
    public string? Login { get; set; }
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? ConfirmPassword { get; set; }
    public string? CaptchaToken { get; set; }
}

public sealed class LoginRequest
{
    public string? LoginOrEmail { get; set; }
    public string? Login { get; set; }
    public string? Email { get; set; }
    public string? Password { get; set; }
    public bool RememberMe { get; set; }
    public string? DeviceName { get; set; }
    public string? Platform { get; set; }
    public string? ClientVersion { get; set; }

    public string GetLoginOrEmail() =>
        (LoginOrEmail ?? Login ?? Email ?? string.Empty).Trim();
}

public sealed class RefreshTokenRequest
{
    public string? RefreshToken { get; set; }
}

public sealed class LogoutRequest
{
    public string? RefreshToken { get; set; }
}

public sealed class ConfirmEmailRequest
{
    public string? Code { get; set; }
}

public record EmailVerificationStatusDto(
    string Email,
    bool EmailConfirmed,
    DateTime? EmailVerifiedAt
);

public record ResendEmailConfirmationResponse(
    bool Sent,
    bool Cooldown,
    int RetryAfterSeconds,
    DateTime? ExpiresAt,
    string Message
);

public record UserSessionDto(
    Guid Id,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? LastSeenAt,
    DateTime? LastRefreshAt,
    string DeviceName,
    string Platform,
    string ClientVersion,
    string IpAddress,
    bool IsCurrent
);

public record UserDto(
    Guid Id,
    string UserName,          // публичное имя для старого клиента
    string Email,
    DateTime CreatedAt,
    string? Login = null,     // приватный логин, отдаётся только самому себе
    string? DisplayName = null,
    string? PublicId = null,
    bool EmailConfirmed = false,
    DateTime? EmailVerifiedAt = null,
    string? AvatarUrl = null
);

public record TokenResponse(
    string Token,
    UserDto User,
    IReadOnlyList<string>? Roles = null,
    string? RefreshToken = null,
    Guid? SessionId = null,
    DateTime? AccessTokenExpiresAt = null,
    DateTime? RefreshTokenExpiresAt = null
);

public record UserSearchDto(
    Guid Id,
    string UserName,          // публичное имя для старого клиента
    string Email,             // больше не заполняем публично, оставлено для совместимости клиента
    string? AvatarUrl,
    string? DisplayName = null,
    string? PublicId = null
);

public record FriendDto(
    Guid Id,
    string UserName,          // публичное имя для старого клиента
    string Email,             // больше не заполняем публично, оставлено для совместимости клиента
    string? AvatarUrl,
    string? DisplayName = null,
    string? PublicId = null
);

public record AttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Url,
    bool IsImage,
    bool IsVideo,
    string ScanStatus = "NotScanned",
    bool IsScanned = false,
    bool IsSafeToDownload = false,
    string? ScanWarning = null
);

public record MessageForwardInfoDto(
    Guid MessageId,
    Guid SenderId,
    string Text,
    DateTime SentAt,
    bool HasAttachments,
    DirectMessageKind Kind,
    string? SystemKey,
    DateTime? DeletedAt
);

public record MessageDto(
    Guid Id,
    Guid SenderId,
    string Text,
    DateTime SentAt,
    IReadOnlyList<AttachmentDto>? Attachments = null,
    DirectMessageKind Kind = DirectMessageKind.User,
    string? SystemKey = null,
    Guid? ForwardedFromMessageId = null,
    DateTime? EditedAt = null,
    DateTime? DeletedAt = null,
    Guid? DeletedById = null,
    MessageForwardInfoDto? ForwardedFrom = null,
    int? GroupSecurityEpoch = null
);

public record UnreadDialogDto(Guid FriendId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt);
public record MarkReadRequest(Guid LastReadMessageId);

public record SendMessageRequest(string? Text, IReadOnlyList<Guid>? FileIds = null);
public record EditMessageRequest(string? Text);
public record ForwardMessagesRequest(IReadOnlyList<Guid> MessageIds, bool IncludeAttachments = true);
public record SendSystemMessageRequest(string SystemKey, string? Text);

public record E2eeDeviceKeyDto(
    Guid Id,
    Guid UserId,
    string DeviceId,
    string PublicKeyBase64,
    string Algorithm,
    string Fingerprint,
    string? DeviceName,
    bool IsTrusted,
    bool IsRevoked,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastSeenAt
);

public record E2eePublicKeyDto(
    Guid UserId,
    string PublicKeyBase64,
    string Algorithm,
    string Fingerprint,
    DateTime UpdatedAt,
    string? DeviceId = null,
    string? DeviceName = null
);

public record UpsertE2eePublicKeyRequest(
    string PublicKeyBase64,
    string? DeviceName = null,
    bool ReplaceExisting = false,
    string? DeviceId = null
);

public record UpsertE2eeDeviceKeyRequest(
    string DeviceId,
    string PublicKeyBase64,
    string? DeviceName = null,
    bool ReplaceExisting = true
);

public record ChatRealtimeMessageDto(Guid PeerId, MessageDto Message);
public record ChatMessageUpdatedDto(Guid PeerId, MessageDto Message);
public record ChatMessageDeletedDto(Guid PeerId, Guid MessageId, DateTime DeletedAt, Guid DeletedById);
public record ChatUnreadChangedDto(Guid FriendId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt);

public record FriendRequestDto(
    Guid RequestId,
    Guid UserId,
    string UserName,
    string Email,
    DateTime CreatedAt,
    string Direction,
    string? DisplayName = null,
    string? PublicId = null
);

public record FileUploadResponse(
    Guid FileId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Url,
    bool IsImage,
    bool IsVideo,
    string ScanStatus = "Pending",
    bool IsScanned = false,
    bool IsSafeToDownload = false,
    string? ScanWarning = null
);


public record CreateGroupChatRequest(string Title, string? Description = null, IReadOnlyList<Guid>? MemberIds = null, bool IsPublic = false);
public record UpdateGroupChatRequest(string Title, string? Description = null, bool? IsPublic = null);
public record UpdateGroupMembersRequest(IReadOnlyList<Guid> UserIds);
public record UpdateGroupMemberRoleRequest(string Role);

public record GroupChatMemberDto(
    Guid UserId,
    string UserName,
    string Email,
    string? AvatarUrl,
    DateTime JoinedAt,
    bool IsOwner,
    GroupChatRole Role,
    string RoleName,
    string RoleColorHex,
    string RoleColorName,
    string? DisplayName = null,
    string? PublicId = null
);

public record GroupChatSummaryDto(
    Guid Id,
    string Title,
    string? Description,
    string? AvatarUrl,
    Guid OwnerId,
    int MemberCount,
    int MemberLimit,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastMessageAt,
    MessageDto? LastMessage,
    GroupChatRole MyRole,
    string MyRoleName,
    string MyRoleColorHex,
    string MyRoleColorName,
    bool CanEditGroup,
    bool CanManageMembers,
    bool CanManageRoles,
    int UnreadCount = 0,
    Guid? FirstUnreadId = null,
    DateTime? FirstUnreadAt = null,
    int SecurityEpoch = 1,
    DateTime? SecurityEpochChangedAt = null,
    bool IsPublic = false,
    bool IsMember = true
);

public record GroupChatDetailsDto(GroupChatSummaryDto Chat, IReadOnlyList<GroupChatMemberDto> Members);

public record PublicGroupSearchDto(
    Guid Id,
    string Title,
    string? Description,
    string? AvatarUrl,
    Guid OwnerId,
    int MemberCount,
    int MemberLimit,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsPublic,
    bool IsMember
);
public record GroupUnreadChatDto(Guid GroupId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt);

public record GroupChatRealtimeMessageDto(Guid GroupId, MessageDto Message);
public record GroupChatMessageUpdatedDto(Guid GroupId, MessageDto Message);
public record GroupChatMessageDeletedDto(Guid GroupId, Guid MessageId, DateTime DeletedAt, Guid DeletedById);
public record GroupChatUnreadChangedDto(Guid GroupId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt);
public record GroupChatUpdatedDto(GroupChatSummaryDto Chat);
public record GroupChatMembersChangedDto(Guid GroupId, IReadOnlyList<GroupChatMemberDto> Members, int SecurityEpoch = 1, DateTime? SecurityEpochChangedAt = null);


public record SetGroupAvatarUrlRequest(string AvatarUrl);
public record CrossChatForwardRequest(string Source, Guid? SourceChatId, IReadOnlyList<Guid> MessageIds, bool IncludeAttachments = true);

public record GroupVoiceJoinRequest(string? ClientInfo = null);

public record GroupVoiceIceServerDto(
    IReadOnlyList<string> Urls,
    string? Username = null,
    string? Credential = null,
    string CredentialType = "password"
);


public record GroupVoiceParticipantDto(
    Guid UserId,
    string UserName,
    string? AvatarUrl,
    DateTime JoinedAt,
    DateTime LastSeenAt,
    bool IsActive,
    string? DisplayName = null,
    string? PublicId = null
);

public record GroupVoiceStateDto(
    Guid GroupId,
    bool IsActive,
    Guid? SessionId,
    string? RoomName,
    Guid? StartedByUserId,
    DateTime? StartedAt,
    int ActiveParticipantCount,
    IReadOnlyList<GroupVoiceParticipantDto> Participants
);

public record GroupVoiceJoinResponse(
    string LiveKitUrl,
    string RoomName,
    string Token,
    Guid SessionId,
    bool IsNewSession,
    GroupVoiceStateDto State,
    IReadOnlyList<GroupVoiceIceServerDto>? IceServers = null,
    string IceTransportPolicy = "all",
    bool PreferTcpTurn = false
);

public record GroupVoiceStateChangedDto(Guid GroupId, GroupVoiceStateDto State);
public record GroupVoiceParticipantChangedDto(Guid GroupId, Guid SessionId, Guid UserId, GroupVoiceStateDto State);


public record GrantUserRoleRequest(Guid UserId, string Role, string? Reason = null);
public record RevokeUserRoleRequest(Guid UserId, string Role, string? Reason = null);

public record UserRoleDto(
    Guid Id,
    Guid UserId,
    GlobalRole Role,
    DateTime GrantedAt,
    Guid? GrantedByUserId,
    string? Reason,
    DateTime? RevokedAt,
    Guid? RevokedByUserId,
    string? RevokeReason);

public sealed record AdminMeDto(
    Guid UserId,
    string PublicId,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles);

public sealed record AdminOverviewDto(
    int TotalUsers,
    int VerifiedUsers,
    int DisabledUsers,
    int OnlineUsers,
    int NewUsersDay,
    int NewUsersWeek,
    int NewUsersMonth,
    int NewUsersHalfYear,
    int NewUsersYear,
    int DirectMessages,
    int GroupMessages,
    int Groups,
    int TotalFiles,
    int PendingFiles,
    int BlockedFiles,
    long TotalStorageBytes,
    IReadOnlyList<AdminRoleCounterDto> Roles,
    DateTime GeneratedAt);

public sealed record AdminRoleCounterDto(string Role, int Count);

public sealed record AdminUsersPageDto(int Total, IReadOnlyList<AdminUserListItemDto> Items);

public sealed record AdminUserListItemDto(
    Guid Id,
    string PublicId,
    string DisplayName,
    string Login,
    string Email,
    bool EmailConfirmed,
    bool IsDisabled,
    string? DisabledReason,
    DateTime CreatedAt,
    DateTime? LastSeen,
    string? AvatarUrl,
    string? ProfileBannerUrl,
    IReadOnlyList<string> Roles);

public sealed record AdminBanUserRequest(Guid UserId, string? Reason = null, string? Type = "Account", DateTime? ExpiresAt = null, bool NotifyEmail = true, string? EmailSubject = null, string? EmailBody = null, Guid? ReportId = null);
public sealed record AdminRevokeBanRequest(string? Reason = null);

public sealed record AdminBanDto(
    Guid Id,
    Guid UserId,
    string PublicId,
    string DisplayName,
    string Email,
    string Type,
    string Reason,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    Guid? CreatedByUserId,
    Guid? RevokedByUserId,
    string? RevokeReason);

public sealed record AdminReportDto(
    Guid Id,
    DateTime CreatedAt,
    string Status,
    string TargetType,
    string TargetId,
    string ReporterPublicId,
    string ReporterDisplayName,
    string Reason,
    string Summary,
    string? TargetUserPublicId = null,
    string? TargetUserDisplayName = null,
    string? TargetGroupTitle = null,
    Guid? TargetUserId = null,
    Guid? TargetMessageId = null,
    Guid? TargetGroupId = null);
public sealed record CreateReportRequest(
    string TargetType,
    Guid? TargetUserId = null,
    Guid? TargetMessageId = null,
    Guid? TargetGroupId = null,
    string? Reason = null,
    string? Details = null);

public sealed record ReportCreatedDto(Guid Id, DateTime CreatedAt, string Status);

public sealed record AdminReportDetailsDto(
    Guid Id,
    DateTime CreatedAt,
    string Status,
    string TargetType,
    string TargetId,
    Guid ReporterUserId,
    string ReporterPublicId,
    string ReporterDisplayName,
    string Reason,
    string Details,
    Guid? TargetUserId,
    string? TargetUserPublicId,
    string? TargetUserDisplayName,
    Guid? TargetMessageId,
    string? MessagePreview,
    Guid? TargetGroupId,
    string? TargetGroupTitle,
    DateTime? ResolvedAt,
    string? ModerationNote);

public sealed record AdminWarnUserRequest(
    Guid UserId,
    Guid? ReportId = null,
    string? Reason = null,
    bool NotifyEmail = true,
    string? EmailSubject = null,
    string? EmailBody = null);

public sealed record AdminReportActionRequest(
    string? Note = null,
    bool NotifyEmail = true,
    string? EmailSubject = null,
    string? EmailBody = null,
    DateTime? ExpiresAt = null);



public sealed record AdminFileThreatDto(
    Guid Id,
    DateTime CreatedAt,
    Guid UploaderId,
    string UploaderPublicId,
    string UploaderDisplayName,
    string Kind,
    long SizeBytes,
    string Bucket,
    string Sha256,
    string ScanStatus,
    bool IsPotentiallyDangerous,
    string? RiskNote,
    DateTime? BlockedAt,
    DateTime? DeletedAt,
    int DirectMessageRefs,
    int GroupMessageRefs,
    int AgeMinutes,
    string LocationText);

public sealed record AdminFileThreatsPageDto(int Total, IReadOnlyList<AdminFileThreatDto> Items);

public sealed record AdminFileThreatActionRequest(string? Reason = null);
