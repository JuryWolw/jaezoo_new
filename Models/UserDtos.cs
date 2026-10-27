using System;
using System.Collections.Generic;

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
        string? ProfileTextTheme = null,
        LastSeenVisibility LastSeenVisibility = LastSeenVisibility.Approximate,
        bool ShowActivity = true,
        string? CurrentActivityName = null,
        DateTime? CurrentActivityUpdatedAt = null
    )
    {
        /// <summary>Плашки у ника («Разработчик», «Тестер», «Партнёр» …), выдаются из админки.</summary>
        public IReadOnlyList<UserBadgeDto>? Badges { get; init; }
    }

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
        string? ProfileTextTheme = null,
        string? About = null,
        LastSeenVisibility LastSeenVisibility = LastSeenVisibility.Approximate,
        string? CurrentActivityName = null,
        DateTime? CurrentActivityUpdatedAt = null
    )
    {
        public IReadOnlyList<UserBadgeDto>? Badges { get; init; }
    }

    /// <summary>Плашка у ника: подпись, цвет (#RRGGBB), значок (символ Segoe MDL2 или эмодзи, может не быть).</summary>
    /// <param name="TextColorHex">Цвет текста; null — старая плашка (текст светлее ColorHex, фон — ColorHex приглушённый).</param>
    public record UserBadgeDto(Guid Id, string Title, string ColorHex, string? Icon, string? TextColorHex = null);

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

    public record UserActivityRequest(
        string? ActivityName
    );

    public record SetAvatarUrlRequest(
        string AvatarUrl
    );
}
