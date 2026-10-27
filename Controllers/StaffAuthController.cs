using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Admin;
using JaeZoo.Server.Services.Email;
using JaeZoo.Server.Services.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

/// <summary>
/// Вход в админку (JaeZoo Control Room) и рекламное окно. Каждый раз: пароль → код с почты → код 2FA (если включена).
/// Только здесь выдаётся токен с отметкой персонала (<see cref="StaffClaims"/>), без которой админские методы закрыты.
/// Токен на 8 часов и без обновления: после истечения — снова полный вход.
/// </summary>
[ApiController]
[Route("api/staff-auth")]
[EnableRateLimiting("auth")]
public sealed class StaffAuthController(
    AppDbContext db,
    TokenService tokens,
    EmailVerificationService emailVerification,
    LoginThrottle throttle,
    StaffLoginChallengeStore challenges,
    AdminAuditService audit,
    SecurityAuditService securityAudit) : ControllerBase
{
    private const int CodeSendsPerWindow = 6;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(8);
    private static readonly PasswordHasher<User> Hasher = new();
    private static readonly string TimingDummyHash = new PasswordHasher<User>().HashPassword(new User(), "jaezoo-staff-timing-equalizer");

    public sealed record StartRequest(string? LoginOrEmail, string? Password, string? App);
    public sealed record StartResponse(string ChallengeToken, bool RequiresTotp, string EmailHint, DateTime ExpiresAt);
    public sealed record ResendRequest(string? ChallengeToken);
    public sealed record VerifyRequest(string? ChallengeToken, string? EmailCode, string? TotpCode);
    public sealed record StaffTokenResponse(string Token, DateTime ExpiresAt, string DisplayName, string PublicId, List<string> Roles, string App);

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartRequest r, CancellationToken ct)
    {
        var app = (r.App ?? string.Empty).Trim().ToLowerInvariant();
        if (app is not (StaffClaims.AdminApp or StaffClaims.AdsApp))
            return BadRequest("Неизвестное приложение.");

        var loginOrEmail = (r.LoginOrEmail ?? string.Empty).Trim();
        var password = r.Password ?? string.Empty;
        if (loginOrEmail.Length == 0 || password.Length == 0)
            return Unauthorized("Неверный логин/почта или пароль.");

        var lookupHash = loginOrEmail.Contains('@') ? IdentityDataProtector.HashEmail(loginOrEmail) : IdentityDataProtector.HashLogin(loginOrEmail);
        var accountKey = LoginThrottle.AccountKey(lookupHash);
        var addressKey = LoginThrottle.AddressKey(UserSessionService.GetRemoteIp(HttpContext));
        if (throttle.RetryAfterAny(accountKey, addressKey) is { } wait)
            return TooMany(wait);

        var user = await db.Users.FirstOrDefaultAsync(u => u.LoginHash == lookupHash || u.EmailHash == lookupHash, ct);
        if (user is null)
        {
            Hasher.VerifyHashedPassword(new User(), TimingDummyHash, password);
            throttle.RegisterFailure(accountKey, LoginThrottle.AccountLimit);
            throttle.RegisterFailure(addressKey, LoginThrottle.AddressLimit);
            return Unauthorized("Неверный логин/почта или пароль.");
        }

        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            throttle.RegisterFailure(accountKey, LoginThrottle.AccountLimit);
            throttle.RegisterFailure(addressKey, LoginThrottle.AddressLimit);
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.StaffLoginFailed", "User", user.Id.ToString(), $"Wrong password ({app}). publicId={user.PublicId}", ct);
            return Unauthorized("Неверный логин/почта или пароль.");
        }
        throttle.Reset(accountKey);

        // Права проверяются только после верного пароля: по ответу нельзя узнать, кто из пользователей — персонал.
        var roles = await ActiveRolesAsync(user.Id, ct);
        if (user.IsDisabled || !HasAccess(roles, app))
        {
            await audit.WriteForUserAsync(user, HttpContext, "StaffLoginDenied", "User", user.Id.ToString(), $"Нет доступа к {AppName(app)}", ct);
            return StatusCode(StatusCodes.Status403Forbidden, $"У этого аккаунта нет доступа к {AppName(app)}.");
        }

        if (!user.EmailConfirmed)
            return BadRequest("Почта аккаунта не подтверждена — код входа отправить некуда. Подтвердите почту в настройках JaeZoo.");

        // Свой лимит на письма: с паролем на руках нельзя завалить почту кодами.
        var mailKey = "staffmail:" + user.Id.ToString("N");
        if (throttle.RetryAfter(mailKey) is { } mailWait)
            return TooMany(mailWait);

        var requiresTotp = user.TwoFactorEnabled && !string.IsNullOrWhiteSpace(user.TwoFactorSecretEncrypted);
        var (token, challenge) = challenges.Create(user.Id, app, requiresTotp);
        var sent = await TrySendCodeAsync(user, challenge.Id, ct);
        if (!sent.Sent)
            return BadRequest(sent.Message);
        throttle.RegisterFailure(mailKey, CodeSendsPerWindow);

        return Ok(new StartResponse(token, requiresTotp, MaskEmail(UserIdentityService.GetEmail(user)), challenge.ExpiresAt));
    }

    [HttpPost("resend")]
    public async Task<IActionResult> Resend([FromBody] ResendRequest r, CancellationToken ct)
    {
        var challenge = challenges.Find(r.ChallengeToken);
        if (challenge is null) return Unauthorized("Вход истёк. Начните заново.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == challenge.UserId, ct);
        if (user is null) return Unauthorized("Вход истёк. Начните заново.");

        var mailKey = "staffmail:" + user.Id.ToString("N");
        if (throttle.RetryAfter(mailKey) is { } mailWait)
            return TooMany(mailWait);
        var sent = await TrySendCodeAsync(user, challenge.Id, ct);
        if (sent.Cooldown) return StatusCode(StatusCodes.Status429TooManyRequests, sent.Message);
        if (!sent.Sent) return BadRequest(sent.Message);
        throttle.RegisterFailure(mailKey, CodeSendsPerWindow);
        return Ok(new { sent.Message });
    }

    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyRequest r, CancellationToken ct)
    {
        var tokenValue = (r.ChallengeToken ?? string.Empty).Trim();
        var challenge = challenges.Find(tokenValue);
        if (challenge is null) return Unauthorized("Вход истёк или попытки кончились. Начните заново.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == challenge.UserId, ct);
        if (user is null || user.IsDisabled) return Unauthorized("Вход истёк. Начните заново.");

        // Сначала 2FA (без сохранения), потом код с почты: неверный код 2FA не сжигает письмо.
        long? acceptedStep = null;
        if (challenge.RequiresTotp)
        {
            var otp = TotpService.NormalizeCode(r.TotpCode);
            var secret = string.IsNullOrWhiteSpace(user.TwoFactorSecretEncrypted) ? null : IdentityDataProtector.UnprotectSecret(user.TwoFactorSecretEncrypted);
            if (secret == null || otp.Length != 6 || !TotpService.TryVerifyTotp(secret, otp, user.TwoFactorLastUsedStep, out var step))
                return await FailAsync(tokenValue, user, "Неверный код из приложения-аутентификатора.", ct);
            acceptedStep = step;
        }

        if (!await emailVerification.TryConsumeTwoFactorLoginCodeAsync(user.Id, r.EmailCode ?? string.Empty, HttpContext, challenge.Id, ct))
            return await FailAsync(tokenValue, user, "Неверный код с почты.", ct);

        if (!challenges.Consume(tokenValue))
            return Unauthorized("Вход истёк. Начните заново.");

        var roles = await ActiveRolesAsync(user.Id, ct);
        if (!HasAccess(roles, challenge.App))
            return StatusCode(StatusCodes.Status403Forbidden, $"У этого аккаунта больше нет доступа к {AppName(challenge.App)}.");

        if (acceptedStep.HasValue)
            user.TwoFactorLastUsedStep = acceptedStep;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var expiresAt = DateTime.UtcNow + TokenLifetime;
        var jwt = tokens.Create(user, roles, sessionId: null, expiresUtc: expiresAt, staffApp: challenge.App);
        await audit.WriteForUserAsync(user, HttpContext, "StaffLogin", "User", user.Id.ToString(),
            $"Вход в {AppName(challenge.App)} (пароль, код с почты{(challenge.RequiresTotp ? ", 2FA" : string.Empty)})", ct);
        return Ok(new StaffTokenResponse(jwt, expiresAt, UserIdentityService.GetPublicName(user), user.PublicId ?? string.Empty,
            roles.Select(x => x.ToString()).ToList(), challenge.App));
    }

    /// <summary>Почтовый сервис недоступен — понятная ошибка вместо 500 (вход персонала без письма невозможен).</summary>
    private async Task<EmailVerificationSendResult> TrySendCodeAsync(User user, Guid challengeId, CancellationToken ct)
    {
        try
        {
            return await emailVerification.SendTwoFactorLoginCodeAsync(user, HttpContext, challengeId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.StaffLoginMailFailed", "User", user.Id.ToString(), ex.GetType().Name, ct);
            return new EmailVerificationSendResult(false, false, 0, null, "Не удалось отправить код на почту. Попробуйте позже.");
        }
    }

    private async Task<IActionResult> FailAsync(string token, User user, string message, CancellationToken ct)
    {
        var exhausted = challenges.RegisterFailure(token);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.StaffLoginCodeFailed", "User", user.Id.ToString(), $"{message} exhausted={exhausted}; publicId={user.PublicId}", ct);
        return Unauthorized(exhausted ? "Слишком много неверных кодов. Начните вход заново." : message);
    }

    private Task<List<GlobalRole>> ActiveRolesAsync(Guid userId, CancellationToken ct)
        => db.UserRoles.Where(x => x.UserId == userId && x.RevokedAt == null).Select(x => x.Role).ToListAsync(ct);

    private static bool HasAccess(List<GlobalRole> roles, string app)
    {
        var names = roles.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
        var allowed = app == StaffClaims.AdminApp ? AuthPolicies.AdminPanelRoles : AuthPolicies.AdsManagerRoles;
        return allowed.Any(names.Contains);
    }

    private static string AppName(string app) => app == StaffClaims.AdminApp ? "Control Room" : "рекламному окну";

    private ObjectResult TooMany(TimeSpan wait)
    {
        Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, $"Слишком много попыток. Попробуйте через {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} мин.");
    }

    /// <summary>f***@gmail.com — чтобы было понятно, куда ушёл код, не раскрывая адрес целиком.</summary>
    internal static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return "вашу почту";
        var at = email.IndexOf('@');
        return email[..1] + "***" + email[at..];
    }
}
