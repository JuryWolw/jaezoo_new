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

    public class User
    {
        public Guid Id { get; set; }

        [MaxLength(64)]
        public string UserName { get; set; } = string.Empty; // логин, как было

        [MaxLength(128)]
        public string Email { get; set; } = string.Empty;

        [MaxLength(256)]
        public string PasswordHash { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // --- НОВОЕ: публичный профиль ---
        [MaxLength(64)]
        public string? DisplayName { get; set; } // ник/дисплейное имя (может повторяться)

        [MaxLength(256)]
        public string? AvatarUrl { get; set; }   // ссылка на картинку (абсолютная или относительная)

        [MaxLength(256)]
        public string? About { get; set; }       // «О себе»

        public UserStatus Status { get; set; } = UserStatus.Offline;
        public bool ShowOnline { get; set; } = true; // можно ли показывать, что юзер онлайн

        [MaxLength(64)]
        public string? CustomStatus { get; set; } // произвольная подпись («Работаю», «AFK»)

        public DateTime? LastSeen { get; set; } // обновляем на каждом авторизованном запросе
    }
}
