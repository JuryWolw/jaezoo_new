namespace JaeZoo.Server.Models;

public record RegisterRequest(string UserName, string Email, string Password, string ConfirmPassword);
public record LoginRequest(string LoginOrEmail, string Password);
public record UserDto(Guid Id, string UserName, string Email, DateTime CreatedAt);
public record TokenResponse(string Token, UserDto User);

public record UserSearchDto(Guid Id, string UserName, string Email, string? AvatarUrl);
public record FriendDto(Guid Id, string UserName, string Email, string? AvatarUrl);

public record AttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Url,
    bool IsImage,
    bool IsVideo
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
    MessageForwardInfoDto? ForwardedFrom = null
);

public record UnreadDialogDto(Guid FriendId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt);
public record MarkReadRequest(Guid LastReadMessageId);

public record SendMessageRequest(string? Text, IReadOnlyList<Guid>? FileIds = null);
public record EditMessageRequest(string? Text);
public record ForwardMessagesRequest(IReadOnlyList<Guid> MessageIds, bool IncludeAttachments = true);
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
    string Direction
);

public record FileUploadResponse(
    Guid FileId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Url,
    bool IsImage,
    bool IsVideo
);


public record CreateGroupChatRequest(string Title, IReadOnlyList<Guid>? MemberIds = null);
public record UpdateGroupChatRequest(string Title);
public record UpdateGroupMembersRequest(IReadOnlyList<Guid> UserIds);

public record GroupChatMemberDto(
    Guid UserId,
    string UserName,
    string Email,
    string? AvatarUrl,
    DateTime JoinedAt,
    bool IsOwner
);

public record GroupChatSummaryDto(
    Guid Id,
    string Title,
    Guid OwnerId,
    int MemberCount,
    int MemberLimit,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastMessageAt,
    MessageDto? LastMessage,
    int UnreadCount = 0,
    Guid? FirstUnreadId = null,
    DateTime? FirstUnreadAt = null
);

public record GroupChatDetailsDto(GroupChatSummaryDto Chat, IReadOnlyList<GroupChatMemberDto> Members);
public record GroupUnreadChatDto(Guid GroupId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt);

public record GroupChatRealtimeMessageDto(Guid GroupId, MessageDto Message);
public record GroupChatMessageUpdatedDto(Guid GroupId, MessageDto Message);
public record GroupChatMessageDeletedDto(Guid GroupId, Guid MessageId, DateTime DeletedAt, Guid DeletedById);
public record GroupChatUnreadChangedDto(Guid GroupId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt);
public record GroupChatUpdatedDto(GroupChatSummaryDto Chat);
public record GroupChatMembersChangedDto(Guid GroupId, IReadOnlyList<GroupChatMemberDto> Members);
