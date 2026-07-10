using System.Security.Cryptography;
using System.Text;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Options;
using JaeZoo.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Email;

public sealed record EmailVerificationSendResult(bool Sent, bool Cooldown, int RetryAfterSeconds, DateTime? ExpiresAt, string Message);

public sealed class EmailVerificationService(
    AppDbContext db,
    IEmailSender emailSender,
    IOptions<PostboxOptions> options,
    ILogger<EmailVerificationService> logger)
{
    private readonly PostboxOptions _options = options.Value;

    public async Task<EmailVerificationSendResult> SendConfirmationCodeAsync(User user, HttpContext http, CancellationToken ct)
    {
        if (user.EmailConfirmed)
            return new EmailVerificationSendResult(false, false, 0, user.EmailVerifiedAt, "Почта уже подтверждена.");

        var now = DateTime.UtcNow;
        var cooldown = Math.Max(10, _options.ResendCooldownSeconds);
        var lastCode = await db.EmailVerificationCodes
            .Where(c => c.UserId == user.Id &&
                        c.Purpose == EmailVerificationPurpose.EmailConfirmation &&
                        c.ConsumedAt == null)
            .OrderByDescending(c => c.LastSentAt)
            .FirstOrDefaultAsync(ct);

        if (lastCode != null && lastCode.LastSentAt.AddSeconds(cooldown) > now)
        {
            var retryAfter = (int)Math.Ceiling((lastCode.LastSentAt.AddSeconds(cooldown) - now).TotalSeconds);
            return new EmailVerificationSendResult(false, true, retryAfter, lastCode.ExpiresAt, $"Повторная отправка будет доступна через {retryAfter} сек.");
        }

        var code = CreateCode();
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var lifetime = Math.Clamp(_options.CodeLifetimeMinutes, 5, 60);

        var entity = new EmailVerificationCode
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Purpose = EmailVerificationPurpose.EmailConfirmation,
            Salt = salt,
            CodeHash = HashCode(user.Id, EmailVerificationPurpose.EmailConfirmation, salt, code),
            CreatedAt = now,
            LastSentAt = now,
            ExpiresAt = now.AddMinutes(lifetime),
            AttemptCount = 0,
            IpAddress = UserSessionService.GetRemoteIp(http),
            UserAgent = UserSessionService.CleanHeader(http.Request.Headers.UserAgent.ToString(), 256)
        };

        db.EmailVerificationCodes.Add(entity);
        await db.SaveChangesAsync(ct);

        try
        {
            // Не привязываем SMTP-отправку к RequestAborted: WPF-клиент может отвалиться по таймауту,
            // а сервер всё равно должен либо нормально отправить письмо, либо очистить созданный код.
            using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await emailSender.SendEmailConfirmationCodeAsync(user, code, sendCts.Token);

            logger.LogInformation("Email confirmation code sent. UserId={UserId} EmailHash={EmailHash}", user.Id, user.EmailHash);
            return new EmailVerificationSendResult(true, false, 0, entity.ExpiresAt, "Код отправлен на почту.");
        }
        catch (Exception ex)
        {
            try
            {
                db.EmailVerificationCodes.Remove(entity);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to cleanup unsent email confirmation code. CodeId={CodeId} UserId={UserId}", entity.Id, user.Id);
            }

            logger.LogError(ex, "Failed to send email confirmation code. UserId={UserId} EmailHash={EmailHash}", user.Id, user.EmailHash);
            throw;
        }
    }


    public async Task<EmailVerificationSendResult> SendTwoFactorLoginCodeAsync(User user, HttpContext http, Guid challengeId, CancellationToken ct)
    {
        if (!user.EmailConfirmed)
            return new EmailVerificationSendResult(false, false, 0, null, "Почта не подтверждена. Используйте recovery-код 2FA.");

        var now = DateTime.UtcNow;
        var purpose = EmailVerificationPurpose.TwoFactorLoginRecovery;
        var cooldown = Math.Max(10, _options.ResendCooldownSeconds);
        var lastCode = await db.EmailVerificationCodes
            .Where(c => c.UserId == user.Id &&
                        c.Purpose == purpose &&
                        c.Salt.StartsWith(challengeId.ToString("N")) &&
                        c.ConsumedAt == null)
            .OrderByDescending(c => c.LastSentAt)
            .FirstOrDefaultAsync(ct);

        if (lastCode != null && lastCode.LastSentAt.AddSeconds(cooldown) > now)
        {
            var retryAfter = (int)Math.Ceiling((lastCode.LastSentAt.AddSeconds(cooldown) - now).TotalSeconds);
            return new EmailVerificationSendResult(false, true, retryAfter, lastCode.ExpiresAt, $"Повторная отправка будет доступна через {retryAfter} сек.");
        }

        var code = CreateCode();
        var salt = challengeId.ToString("N") + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var lifetime = Math.Clamp(_options.CodeLifetimeMinutes, 5, 60);

        var entity = new EmailVerificationCode
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Purpose = purpose,
            Salt = salt,
            CodeHash = HashCode(user.Id, purpose, salt, code),
            CreatedAt = now,
            LastSentAt = now,
            ExpiresAt = now.AddMinutes(lifetime),
            AttemptCount = 0,
            IpAddress = UserSessionService.GetRemoteIp(http),
            UserAgent = UserSessionService.CleanHeader(http.Request.Headers.UserAgent.ToString(), 256)
        };

        db.EmailVerificationCodes.Add(entity);
        await db.SaveChangesAsync(ct);

        try
        {
            using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await emailSender.SendTwoFactorLoginCodeAsync(user, code, sendCts.Token);

            logger.LogInformation("2FA email login code sent. UserId={UserId} EmailHash={EmailHash}", user.Id, user.EmailHash);
            return new EmailVerificationSendResult(true, false, 0, entity.ExpiresAt, "Код отправлен на почту.");
        }
        catch (Exception ex)
        {
            try
            {
                db.EmailVerificationCodes.Remove(entity);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to cleanup unsent 2FA email login code. CodeId={CodeId} UserId={UserId}", entity.Id, user.Id);
            }

            logger.LogError(ex, "Failed to send 2FA email login code. UserId={UserId} EmailHash={EmailHash}", user.Id, user.EmailHash);
            throw;
        }
    }

    public async Task<bool> TryConsumeTwoFactorLoginCodeAsync(Guid userId, string code, HttpContext http, Guid challengeId, CancellationToken ct)
    {
        code = (code ?? string.Empty).Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
            return false;

        var now = DateTime.UtcNow;
        var purpose = EmailVerificationPurpose.TwoFactorLoginRecovery;
        var entity = await db.EmailVerificationCodes
            .Where(c => c.UserId == userId &&
                        c.Purpose == purpose &&
                        c.Salt.StartsWith(challengeId.ToString("N")) &&
                        c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (entity == null || entity.ExpiresAt <= now)
            return false;

        var maxAttempts = Math.Clamp(_options.MaxAttempts, 3, 10);
        if (entity.AttemptCount >= maxAttempts)
            return false;

        var expected = HashCode(userId, entity.Purpose, entity.Salt, code);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(entity.CodeHash)))
        {
            entity.AttemptCount += 1;
            await db.SaveChangesAsync(ct);
            return false;
        }

        entity.ConsumedAt = now;
        entity.IpAddress = UserSessionService.GetRemoteIp(http);
        entity.UserAgent = UserSessionService.CleanHeader(http.Request.Headers.UserAgent.ToString(), 256);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<User> ConfirmEmailAsync(Guid userId, string code, CancellationToken ct)
    {
        code = (code ?? string.Empty).Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
            throw new InvalidOperationException("Введите 6-значный код.");

        var now = DateTime.UtcNow;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new InvalidOperationException("Пользователь не найден.");

        if (user.EmailConfirmed)
            return user;

        var entity = await db.EmailVerificationCodes
            .Where(c => c.UserId == userId &&
                        c.Purpose == EmailVerificationPurpose.EmailConfirmation &&
                        c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (entity == null)
            throw new InvalidOperationException("Код не найден. Запросите новый код.");

        if (entity.ExpiresAt <= now)
            throw new InvalidOperationException("Код истёк. Запросите новый код.");

        var maxAttempts = Math.Clamp(_options.MaxAttempts, 3, 10);
        if (entity.AttemptCount >= maxAttempts)
            throw new InvalidOperationException("Слишком много попыток. Запросите новый код.");

        var expected = HashCode(userId, entity.Purpose, entity.Salt, code);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(entity.CodeHash)))
        {
            entity.AttemptCount += 1;
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Неверный код.");
        }

        entity.ConsumedAt = now;
        user.EmailConfirmed = true;
        user.EmailVerifiedAt = now;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return user;
    }

    private static string CreateCode()
    {
        var value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString("D6");
    }

    private static string HashCode(Guid userId, EmailVerificationPurpose purpose, string salt, string code)
    {
        var raw = $"{userId:N}:{(int)purpose}:{salt}:{code}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
