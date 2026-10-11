using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Chat;

/// <summary>
/// Системный чат «JaeZoo»: служебный пользователь с фиксированным Id, который есть в друзьях у всех.
/// Чат только для чтения: пишет в него лишь сервер (уведомления о входе, рассылки из админки).
/// Сообщения — обычный текст (Kind = System), сквозное шифрование здесь не применяется:
/// это публичные служебные объявления, а не переписка пользователей.
/// </summary>
public sealed class SystemChatService(AppDbContext db, DirectChatService chat, IHubContext<ChatHub> hub, ILogger<SystemChatService> logger)
{
    public static readonly Guid SystemUserId = Guid.Parse("6a5e2000-0000-4000-8000-00000000a001");
    public const string SystemDisplayName = "JaeZoo";
    public const string SystemAbout = "Официальный системный чат JaeZoo";
    public const string LoginKey = "jz.login";
    public const string BroadcastKey = "jz.broadcast";

    public static bool IsSystemUser(Guid userId) => userId == SystemUserId;

    /// <summary>Создаёт системного пользователя (если его нет) и дружбу со всеми пользователями. Вызывается при старте.</summary>
    public static async Task EnsureAccountAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == SystemUserId, ct);
            if (user is null)
            {
                var now = DateTime.UtcNow;
                var login = "jaezoo.system";
                var email = "system@jaezoo.invalid";
                user = new User
                {
                    Id = SystemUserId,
                    LoginHash = IdentityDataProtector.HashLogin(login),
                    LoginEncrypted = IdentityDataProtector.ProtectLogin(login),
                    EmailHash = IdentityDataProtector.HashEmail(email),
                    EmailEncrypted = IdentityDataProtector.ProtectEmail(email),
                    IdentityPrivacyVersion = 1,
                    EmailConfirmed = true,
                    EmailVerifiedAt = now,
                    PublicId = "JZ-SYSTEM",
                    DisplayName = SystemDisplayName,
                    About = SystemAbout,
                    CreatedAt = now,
                    UpdatedAt = now,
                    SecurityStamp = UserIdentityService.NewSecurityStamp(),
                    // Пустой хэш пароля: войти под системным пользователем невозможно.
                    PasswordHash = string.Empty,
                    ShowOnline = false,
                    ShowActivity = false,
                    CallStatusVisibility = 0
                };
                IdentityDataProtector.SetLegacyLoginPlaceholder(user);
                IdentityDataProtector.SetLegacyEmailPlaceholder(user);
                db.Users.Add(user);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("System chat user created.");
            }

            var withFriendship = db.Friendships
                .Where(f => f.RequesterId == SystemUserId || f.AddresseeId == SystemUserId)
                .Select(f => f.RequesterId == SystemUserId ? f.AddresseeId : f.RequesterId);

            var missing = await db.Users.AsNoTracking()
                .Where(u => u.Id != SystemUserId && !withFriendship.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);

            if (missing.Count == 0)
                return;

            var at = DateTime.UtcNow;
            foreach (var id in missing)
                db.Friendships.Add(new Friendship { Id = Guid.NewGuid(), RequesterId = SystemUserId, AddresseeId = id, Status = FriendshipStatus.Accepted, CreatedAt = at });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("System chat friendship added for {Count} users.", missing.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to ensure system chat account.");
        }
    }

    /// <summary>Дружба с системным чатом (для пользователей, зарегистрированных после старта сервера).</summary>
    public async Task EnsureFriendshipAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty || IsSystemUser(userId))
            return;

        var exists = await db.Friendships.AnyAsync(f =>
            (f.RequesterId == SystemUserId && f.AddresseeId == userId) ||
            (f.RequesterId == userId && f.AddresseeId == SystemUserId), ct);
        if (exists)
            return;

        if (!await db.Users.AnyAsync(u => u.Id == SystemUserId, ct))
            return;

        db.Friendships.Add(new Friendship { Id = Guid.NewGuid(), RequesterId = SystemUserId, AddresseeId = userId, Status = FriendshipStatus.Accepted, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Сообщение от JaeZoo пользователю (с картинками из админки, если есть). Сразу отправляет его в клиент.</summary>
    public async Task<MessageDto?> PostAsync(Guid userId, string systemKey, string text, IReadOnlyList<Guid>? fileIds = null, CancellationToken ct = default)
    {
        if (userId == Guid.Empty || IsSystemUser(userId))
            return null;

        await EnsureFriendshipAsync(userId, ct);
        var dialog = await chat.GetOrCreateDialogAsync(SystemUserId, userId, ct);
        var now = DateTime.UtcNow;
        var message = new DirectMessage
        {
            DialogId = dialog.Id,
            SenderId = SystemUserId,
            Text = (text ?? string.Empty).Trim(),
            SentAt = now,
            Kind = DirectMessageKind.System,
            SystemKey = systemKey
        };
        db.DirectMessages.Add(message);

        // Один и тот же файл прикрепляется ко многим сообщениям рассылки: доступ к нему
        // проверяется по участию в диалоге, поэтому каждый получатель видит только свою копию сообщения.
        foreach (var fileId in (fileIds ?? Array.Empty<Guid>()).Distinct().Take(10))
            db.DirectMessageAttachments.Add(new DirectMessageAttachment { MessageId = message.Id, FileId = fileId, CreatedAt = now });

        await db.SaveChangesAsync(ct);

        var dto = await chat.GetMessageDtoAsync(dialog.Id, message.Id, ct);
        if (dto is not null)
        {
            try
            {
                await hub.Clients.User(userId.ToString()).SendAsync("ChatMessageCreated", new ChatRealtimeMessageDto(SystemUserId, dto), ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "System chat realtime delivery failed.");
            }
        }

        return dto;
    }

    /// <summary>Уведомление о входе в аккаунт (без IP целиком — только устройство, платформа и время).</summary>
    public async Task TryPostLoginNoticeAsync(Guid userId, LoginNotificationContext context, CancellationToken ct = default)
    {
        try
        {
            var device = string.IsNullOrWhiteSpace(context.DeviceName) ? "неизвестное устройство" : context.DeviceName.Trim();
            var platform = string.IsNullOrWhiteSpace(context.Platform) ? null : context.Platform.Trim();
            var lines = new List<string>
            {
                context.IsKnownDevice ? "Вход в аккаунт" : "Вход с нового устройства",
                platform is null ? $"Устройство: {device}" : $"Устройство: {device} · {platform}"
            };
            if (context.UsedTwoFactor)
                lines.Add(context.UsedRecoveryCode ? "Подтверждено резервным кодом" : "Подтверждено двухфакторной защитой");
            lines.Add("Если это были не вы — смените пароль и завершите чужие сеансы в Настройках → Безопасность.");

            await PostAsync(userId, LoginKey, string.Join('\n', lines), null, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to post login notice to the system chat.");
        }
    }
}
