using System;

namespace JaeZoo.Server.Models
{
    // Приватный профиль (для себя)
    public record UserProfileDto(
        Guid Id,
        string UserName,
        string Email,
        string? DisplayName,
        string? AvatarUrl,
        string? About,
        UserStatus Status,
        string? CustomStatus,
        DateTime CreatedAt,
        DateTime? LastSeen
    );

    // Публичный профиль (для других)
    public record PublicUserDto(
        Guid Id,
        string UserName,
        string? DisplayName,
        string? AvatarUrl,
        UserStatus Status,
        string? CustomStatus,
        DateTime? LastSeen
    );

    // Обновления профиля
    public record UpdateProfileRequest(
        string? DisplayName,
        string? About
    );

    public record UpdateStatusRequest(
        UserStatus Status,
        string? CustomStatus
    );

    public record SetAvatarUrlRequest(
        string AvatarUrl
    );
}
