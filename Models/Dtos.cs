namespace JaeZoo.Server.Models;

public record RegisterRequest(string UserName, string Email, string Password, string ConfirmPassword);
public record LoginRequest(string LoginOrEmail, string Password);
public record UserDto(Guid Id, string UserName, string Email, DateTime CreatedAt);
public record TokenResponse(string Token, UserDto User);

// Для списков/поиска на клиенте важно иметь AvatarUrl (иначе приходится делать лишние запросы).
// AvatarUrl может быть null — тогда клиент использует fallback /avatars/{id}.
public record UserSearchDto(Guid Id, string UserName, string Email, string? AvatarUrl);
public record FriendDto(Guid Id, string UserName, string Email, string? AvatarUrl);

public record MessageDto(Guid SenderId, string Text, DateTime SentAt);

// Заявки в друзья (входящие/исходящие)
public record FriendRequestDto(
    Guid RequestId,
    Guid UserId,
    string UserName,
    string Email,
    DateTime CreatedAt,
    string Direction // "incoming" | "outgoing"
);
