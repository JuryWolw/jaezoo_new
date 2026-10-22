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

public record UserLoginHistoryDto(
    Guid Id,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? LastSeenAt,
    DateTime? LastRefreshAt,
    DateTime? RevokedAt,
    string DeviceName,
    string Platform,
    string ClientVersion,
    string IpAddress,
    bool IsCurrent,
    bool IsActive
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


/// <summary>
/// User messages carry only <see cref="Ciphertext"/> (MLS PrivateMessage); text, reply, forward and
/// attachment keys are inside it. <see cref="Text"/> is used only by Kind = System notices.
/// </summary>
public record MessageDto(
    Guid Id,
    Guid SenderId,
    string Text,
    DateTime SentAt,
    IReadOnlyList<AttachmentDto>? Attachments = null,
    DirectMessageKind Kind = DirectMessageKind.User,
    string? SystemKey = null,
    DateTime? EditedAt = null,
    DateTime? DeletedAt = null,
    Guid? DeletedById = null,
    byte[]? Ciphertext = null,
    long MlsEpoch = 0,
    Guid? SenderDeviceId = null,
    Guid? MlsGroupId = null
);

public record UnreadDialogDto(Guid FriendId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt)
{
    public Guid? LastReadByFriendMessageId { get; init; }
}

public record ChatMessageReadDto(Guid PeerId, Guid ReaderId, Guid LastReadMessageId, DateTime ReadAtUtc);
public record GroupChatMessageReadDto(Guid GroupId, Guid ReaderId, Guid LastReadMessageId, DateTime ReadAtUtc);

public record MarkReadRequest(Guid LastReadMessageId);

/// <summary>New user message: MLS ciphertext built on <paramref name="Epoch"/> by device <paramref name="DeviceId"/>.</summary>
public record SendMessageRequest(byte[] Ciphertext, long Epoch, Guid DeviceId, IReadOnlyList<Guid>? FileIds = null);
public record EditMessageRequest(byte[] Ciphertext, long Epoch, Guid DeviceId);
public record SendSystemMessageRequest(string SystemKey, string? Text);


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
    bool IsPublic = false,
    bool IsMember = true
)
{
    /// <summary>Группа или канал.</summary>
    public GroupChatKind Kind { get; init; }
    /// <summary>Канал: подпись автора под постами.</summary>
    public bool SignPosts { get; init; }
    public Guid? PinnedMessageId { get; init; }
    /// <summary>Канал: может ли пользователь публиковать посты (владелец, админ, модератор).</summary>
    public bool CanPost { get; init; } = true;
    /// <summary>Канал: может ли банить и удалять чужие посты (владелец, админ, модератор).</summary>
    public bool CanModerate { get; init; }
    /// <summary>Шапка группы или канала (null — нет).</summary>
    public string? BannerUrl { get; init; }
}

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
)
{
    public GroupChatKind Kind { get; init; }
}
public record GroupUnreadChatDto(Guid GroupId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt)
{
    public Guid? LastReadByOtherMessageId { get; init; }
}

public record GroupChatRealtimeMessageDto(Guid GroupId, MessageDto Message);
public record GroupChatMessageUpdatedDto(Guid GroupId, MessageDto Message);
public record GroupChatMessageDeletedDto(Guid GroupId, Guid MessageId, DateTime DeletedAt, Guid DeletedById);
public record GroupChatUnreadChangedDto(Guid GroupId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt);
public record GroupChatUpdatedDto(GroupChatSummaryDto Chat);
public record GroupChatMembersChangedDto(Guid GroupId, IReadOnlyList<GroupChatMemberDto> Members);


public record SetGroupAvatarUrlRequest(string AvatarUrl);

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


public sealed record TwoFactorStatusDto(
    bool Enabled,
    DateTime? EnabledAt,
    int RecoveryCodesRemaining
);

public sealed record TwoFactorSetupResponse(
    string ManualKey,
    string OtpAuthUri,
    DateTime ExpiresAtUtc
);

public sealed record TwoFactorEnableRequest(string Code);
public sealed record TwoFactorDisableRequest(string Password, string Code);
public sealed record TwoFactorRegenerateRecoveryCodesRequest(string Password, string Code);

public sealed record TwoFactorEnableResponse(
    bool Enabled,
    IReadOnlyList<string> RecoveryCodes
);

public sealed record TwoFactorRequiredResponse(
    string Code,
    string Message,
    string ChallengeToken,
    DateTime ExpiresAtUtc,
    string PublicId,
    string DisplayName
);

public sealed record TwoFactorLoginRequest(
    string ChallengeToken,
    string Code
);

public sealed record TwoFactorEmailCodeRequest(
    string ChallengeToken
);

public sealed record TwoFactorEmailCodeResponse(
    bool Sent,
    bool Cooldown,
    int RetryAfterSeconds,
    DateTime? ExpiresAtUtc,
    string Message
);


public record FileCloneRequest(IReadOnlyList<Guid> FileIds);
public record FileCloneResult(Guid SourceId, Guid FileId);

// ---------- Каналы ----------

public record CreateChannelRequest(string Title, string? Description = null, bool IsPublic = true, bool SignPosts = false);
public record UpdateChannelRequest(string Title, string? Description = null, bool SignPosts = false);

/// <summary>Пост открытого канала: открытый текст (публичный контент) и незашифрованные вложения.</summary>
public record PublicPostRequest(string? Text, IReadOnlyList<Guid>? FileIds = null);
public record EditPublicPostRequest(string Text);

public record PinPostRequest(Guid? MessageId);
public record MarkPostsViewedRequest(IReadOnlyList<Guid> MessageIds);

/// <summary>Лента канала: посты и число просмотров каждого.</summary>
public record ChannelPostsDto(IReadOnlyList<MessageDto> Posts, IReadOnlyDictionary<Guid, int> Views)
{
    /// <summary>Реакции на посты открытого канала (у закрытого не бывает — сервер не должен их видеть).</summary>
    public IReadOnlyDictionary<Guid, PostReactionsDto>? Reactions { get; init; }
}

/// <summary>Реакции поста: сколько каждой и какие поставил сам пользователь.</summary>
public record PostReactionsDto(IReadOnlyDictionary<string, int> Counts, IReadOnlyList<string> Mine);

public record ReactRequest(string Kind);

/// <summary>SignalR ChannelReactionsChanged: новые счётчики поста (без «моих» — у каждого свои).</summary>
public record ChannelReactionsChangedDto(Guid ChatId, Guid MessageId, IReadOnlyDictionary<string, int> Counts);

/// <summary>Канал глазами пользователя (в том числе ещё не вступившего в открытый канал).</summary>
public record ChannelDetailsDto(GroupChatSummaryDto Channel, bool IsMember, bool IsBanned);

public record BanMemberRequest(string? Reason);
public record ChannelBanDto(Guid UserId, string UserName, string? AvatarUrl, DateTime CreatedAt, string? Reason);

public record CreateInviteRequest(int? ExpiresInHours = null, int? MaxUses = null);
public record InviteDto(Guid Id, string Code, string Url, DateTime CreatedAt, DateTime? ExpiresAt, int? MaxUses, int Uses, bool IsActive);

/// <summary>Предпросмотр по ссылке-приглашению: что за чат, сколько участников, состоит ли уже пользователь.</summary>
public record InvitePreviewDto(Guid ChatId, GroupChatKind Kind, string Title, string? Description, string? AvatarUrl, int MemberCount, bool IsMember);
