namespace JaeZoo.Server.Models;

public record RegisterRequest(string UserName, string Email, string Password, string ConfirmPassword);
public record LoginRequest(string LoginOrEmail, string Password);
public record UserDto(Guid Id, string UserName, string Email, DateTime CreatedAt);
public record TokenResponse(string Token, UserDto User);

// Для списков/поиска на клиенте важно иметь AvatarUrl.
// AvatarUrl может быть null/пустым — тогда клиент использует fallback /avatars/{id}.
public record UserSearchDto(Guid Id, string UserName, string Email, string? AvatarUrl);
public record FriendDto(Guid Id, string UserName, string Email, string? AvatarUrl);

// ВАЖНО: Id нужен для надёжной дедупликации и для read-cursor (непр...
public record MessageDto(Guid Id, Guid SenderId, string Text, DateTime SentAt);

// Непрочитанные по каждому диалогу (1:1) относительно текущего пользователя.
public record UnreadDialogDto(Guid FriendId, int UnreadCount, Guid? FirstUnreadId, DateTime? FirstUnreadAt);

// Отметить диалог прочитанным до заданного курсора...
public record MarkReadRequest(Guid LastReadMessageId, DateTime LastReadAt);

// Заявки в друзья (входящие/исходящие)
public record FriendRequestDto(
    Guid RequestId,
    Guid UserId,
    string UserName,
    string Email,
    DateTime CreatedAt,
    string Direction // "incoming" | "outgoing"
);
