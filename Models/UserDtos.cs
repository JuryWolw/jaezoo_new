using System;

namespace JaeZoo.Server.Models
{
    // Приватный профиль (для себя)
    public record UserProfileDto(
        Guid Id,
        string Login,
        string Email,
        string? DisplayName,
        string? AvatarUrl,
        string? About,
        UserStatus Status,
        string? CustomStatus,
        DateTime CreatedAt,
        DateTime? LastSeen,
        string PublicId,
        bool EmailConfirmed,
        DateTime? EmailVerifiedAt,
        string? ProfileBannerUrl = null,
        string? ProfileTextTheme = null
    );

    // Публичный профиль (для других)
    public record PublicUserDto(
        Guid Id,
        string PublicId,
        string? DisplayName,
        string? AvatarUrl,
        UserStatus Status,
        string? CustomStatus,
        DateTime? LastSeen,
        string? ProfileBannerUrl = null,
        string? ProfileTextTheme = null
    );

    public record UserAvatarDto(
        Guid Id,
        string Url,
        bool IsCurrent,
        DateTime CreatedAt
    );

    public record ProfileBannerDto(
        string? Url,
        string TextTheme
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
