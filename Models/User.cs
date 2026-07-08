using System;
using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models
{
    public enum UserStatus
    {
        Offline = 0,
        Online = 1,
        Busy = 2,
        Away = 3,
        Invisible = 4
    }

    public enum LastSeenVisibility
    {
        Exact = 0,
        Approximate = 1,
        Hidden = 2
    }

    public class User
    {
        public Guid Id { get; set; }

        [MaxLength(64)]
        public string UserName { get; set; } = string.Empty; // legacy: старое имя поля, временно держим как login для совместимости

        [MaxLength(64)]
        public string Login { get; set; } = string.Empty; // приватный логин для входа

        [MaxLength(64)]
        public string LoginNormalized { get; set; } = string.Empty;

        [MaxLength(128)]
        public string Email { get; set; } = string.Empty;

        [MaxLength(128)]
        public string EmailNormalized { get; set; } = string.Empty;

        [MaxLength(128)]
        public string LoginHash { get; set; } = string.Empty;

        [MaxLength(1024)]
        public string LoginEncrypted { get; set; } = string.Empty;

        [MaxLength(128)]
        public string EmailHash { get; set; } = string.Empty;

        [MaxLength(1024)]
        public string EmailEncrypted { get; set; } = string.Empty;

        public int IdentityPrivacyVersion { get; set; } = 0;

        public bool EmailConfirmed { get; set; } = false;
        public DateTime? EmailVerifiedAt { get; set; }

        [MaxLength(32)]
        public string PublicId { get; set; } = string.Empty; // публичный стабильный ID, например JZ-ABCD-1234

        [MaxLength(256)]
        public string PasswordHash { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public bool IsDisabled { get; set; } = false;

        [MaxLength(256)]
        public string? DisabledReason { get; set; }

        [MaxLength(64)]
        public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

        public int TokenVersion { get; set; } = 0;

        // --- публичный профиль ---
        [MaxLength(64)]
        public string? DisplayName { get; set; } // ник/дисплейное имя (может повторяться)

        [MaxLength(256)]
        public string? AvatarUrl { get; set; }   // ссылка на картинку (абсолютная или относительная)

        [MaxLength(512)]
        public string? ProfileBannerUrl { get; set; }

        [MaxLength(16)]
        public string? ProfileTextTheme { get; set; } = "Light"; // Light/Dark для текста поверх шапки

        [MaxLength(256)]
        public string? About { get; set; }       // «О себе»

        public UserStatus Status { get; set; } = UserStatus.Offline;
        public bool ShowOnline { get; set; } = true; // можно ли показывать, что юзер онлайн
        public LastSeenVisibility LastSeenVisibility { get; set; } = LastSeenVisibility.Approximate;
        public bool ShowActivity { get; set; } = true;

        [MaxLength(64)]
        public string? CustomStatus { get; set; } // произвольная подпись («Работаю», «AFK»)

        [MaxLength(96)]
        public string? CurrentActivityName { get; set; }
        public DateTime? CurrentActivityUpdatedAt { get; set; }

        public DateTime? LastSeen { get; set; } // обновляем на каждом авторизованном запросе
    }
}
