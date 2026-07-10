using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Email;
using JaeZoo.Server.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController(
    AppDbContext db,
    TokenService tokens,
    EmailVerificationService emailVerification,
    LoginNotificationService loginNotifications,
    SmartCaptchaService captcha,
    SecurityAuditService securityAudit,
    ILogger<AuthController> log) : ControllerBase
{
    private readonly PasswordHasher<User> _hasher = new();

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest r, CancellationToken ct)
    {
        var login = (r.Login ?? string.Empty).Trim();
        var email = (r.Email ?? string.Empty).Trim();
        var password = r.Password ?? string.Empty;
        var confirmPassword = r.ConfirmPassword ?? string.Empty;

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(confirmPassword))
            return BadRequest("Заполните логин, почту и пароль.");

        if (!UserIdentityService.IsValidLogin(login))
            return BadRequest("Логин должен быть 3-32 символа: латиница, цифры, точка, дефис или подчёркивание.");

        if (!new EmailAddressAttribute().IsValid(email))
            return BadRequest("Некорректный адрес почты.");

        if (password.Length < 8)
            return BadRequest("Пароль должен быть не короче 8 символов.");

        if (password != confirmPassword)
            return BadRequest("Пароли не совпадают.");

        var captchaResult = await captcha.ValidateAsync(r.CaptchaToken, HttpContext, ct);
        if (!captchaResult.Success)
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.RegisterCaptchaFailed", "Identity", SecurityAuditService.HashTarget(login + "|" + email), captchaResult.Message, ct);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "captcha_required",
                message = captchaResult.Message
            });
        }

        var loginHash = IdentityDataProtector.HashLogin(login);
        var emailHash = IdentityDataProtector.HashEmail(email);

        if (await db.Users.AnyAsync(u => u.LoginHash == loginHash, ct))
            return Conflict("Пользователь с таким логином уже существует.");

        if (await db.Users.AnyAsync(u => u.EmailHash == emailHash, ct))
            return Conflict("Пользователь с такой почтой уже существует.");

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = string.Empty,
            Login = string.Empty,
            LoginNormalized = string.Empty,
            LoginHash = loginHash,
            LoginEncrypted = IdentityDataProtector.ProtectLogin(login),
            Email = string.Empty,
            EmailNormalized = string.Empty,
            EmailHash = emailHash,
            EmailEncrypted = IdentityDataProtector.ProtectEmail(email),
            IdentityPrivacyVersion = 1,
            EmailConfirmed = false,
            EmailVerifiedAt = null,
            PublicId = await UserIdentityService.CreateUniquePublicIdAsync(db, ct),
            DisplayName = UserIdentityService.CreateRandomDisplayName(),
            AvatarUrl = null,
            CreatedAt = now,
            UpdatedAt = now,
            SecurityStamp = UserIdentityService.NewSecurityStamp(),
            TokenVersion = 0,
            IsDisabled = false
        };

        IdentityDataProtector.SetLegacyLoginPlaceholder(user);
        IdentityDataProtector.SetLegacyEmailPlaceholder(user);

        user.PasswordHash = _hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.UserRegistered", "User", user.Id.ToString(), $"Registered publicId={user.PublicId}; emailConfirmed=false", ct);

        var emailCodeSent = false;
        var emailMessage = "Код подтверждения будет отправлен после входа.";
        try
        {
            var send = await emailVerification.SendConfirmationCodeAsync(user, HttpContext, ct);
            emailCodeSent = send.Sent;
            emailMessage = send.Message;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to send initial email confirmation code. UserId={UserId} EmailHash={EmailHash}", user.Id, user.EmailHash);
        }

        return Created("", new
        {
            message = emailCodeSent
                ? "Регистрация успешна. Код подтверждения отправлен на почту."
                : "Регистрация успешна. Войдите и запросите код подтверждения почты.",
            publicId = user.PublicId,
            displayName = user.DisplayName,
            emailConfirmed = user.EmailConfirmed,
            emailCodeSent,
            emailMessage
        });
    }

    [HttpPost("login")]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest r, CancellationToken ct)
    {
        var loginOrEmail = r.GetLoginOrEmail();
        var password = r.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(loginOrEmail) || string.IsNullOrWhiteSpace(password))
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.LoginFailed", "Identity", "empty", "Missing login/email or password.", ct);
            return Unauthorized("Неверный логин/почта или пароль.");
        }

        var lookupHash = loginOrEmail.Contains('@')
            ? IdentityDataProtector.HashEmail(loginOrEmail)
            : IdentityDataProtector.HashLogin(loginOrEmail);

        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.LoginHash == lookupHash ||
            u.EmailHash == lookupHash, ct);

        if (user is null)
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.LoginFailed", "Identity", SecurityAuditService.HashTarget(loginOrEmail), "Unknown login/email or wrong password.", ct);
            return Unauthorized("Неверный логин/почта или пароль.");
        }

        if (user.IsDisabled)
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.LoginBlockedDisabled", "User", user.Id.ToString(), $"Disabled account login blocked. publicId={user.PublicId}", ct);
            return StatusCode(StatusCodes.Status403Forbidden, string.IsNullOrWhiteSpace(user.DisabledReason)
                ? "Аккаунт отключён."
                : user.DisabledReason);
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result is PasswordVerificationResult.Failed)
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.LoginFailed", "User", user.Id.ToString(), $"Wrong password. publicId={user.PublicId}", ct);
            return Unauthorized("Неверный логин/почта или пароль.");
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            IdentityDataProtector.SetLegacyLoginPlaceholder(user);
        IdentityDataProtector.SetLegacyEmailPlaceholder(user);

        user.PasswordHash = _hasher.HashPassword(user, password);

        if (string.IsNullOrWhiteSpace(user.LoginHash))
            IdentityDataProtector.SetLogin(user, UserIdentityService.GetLogin(user));
        if (string.IsNullOrWhiteSpace(user.EmailHash))
            IdentityDataProtector.SetEmail(user, UserIdentityService.GetEmail(user));
        user.DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? UserIdentityService.CreateRandomDisplayName() : user.DisplayName;
        user.PublicId = string.IsNullOrWhiteSpace(user.PublicId) ? await UserIdentityService.CreateUniquePublicIdAsync(db, ct) : user.PublicId;
        user.SecurityStamp = string.IsNullOrWhiteSpace(user.SecurityStamp) ? UserIdentityService.NewSecurityStamp() : user.SecurityStamp;
        user.UpdatedAt = DateTime.UtcNow;

        if (user.TwoFactorEnabled && !string.IsNullOrWhiteSpace(user.TwoFactorSecretEncrypted))
        {
            var challengeToken = TotpService.NewLoginChallengeToken();
            var challenge = new TwoFactorLoginChallenge
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ChallengeTokenHash = TotpService.HashLoginChallengeToken(challengeToken),
                RememberMe = r.RememberMe,
                DeviceName = UserSessionService.CleanHeader(r.DeviceName, 128),
                Platform = UserSessionService.CleanHeader(r.Platform, 64),
                ClientVersion = UserSessionService.CleanHeader(r.ClientVersion, 32),
                IpAddress = UserSessionService.GetRemoteIp(HttpContext),
                UserAgent = UserSessionService.CleanHeader(Request.Headers.UserAgent.ToString(), 256),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            };
            db.TwoFactorLoginChallenges.Add(challenge);
            await db.SaveChangesAsync(ct);
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.LoginRequires2FA", "User", user.Id.ToString(), $"Password accepted. 2FA required. publicId={user.PublicId}", ct);

            return StatusCode(428, new TwoFactorRequiredResponse(
                "two_factor_required",
                "Введите код из приложения-аутентификатора.",
                challengeToken,
                challenge.ExpiresAt,
                user.PublicId,
                UserIdentityService.GetPublicName(user)));
        }

        var roles = await db.UserRoles
            .Where(role => role.UserId == user.Id && role.RevokedAt == null)
            .Select(role => role.Role)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var ipAddress = UserSessionService.GetRemoteIp(HttpContext);
        var userAgent = UserSessionService.CleanHeader(Request.Headers.UserAgent.ToString(), 256);
        var deviceName = UserSessionService.CleanHeader(r.DeviceName, 128);
        var platform = UserSessionService.CleanHeader(r.Platform, 64);
        var clientVersion = UserSessionService.CleanHeader(r.ClientVersion, 32);
        var knownDevice = await IsKnownLoginDeviceAsync(user.Id, deviceName, platform, clientVersion, ct);

        UserSession? session = null;
        string? refreshToken = null;
        DateTime? refreshExpiresAt = null;

        if (r.RememberMe)
        {
            refreshToken = UserSessionService.NewRefreshToken();
            refreshExpiresAt = now.AddDays(30);
            session = new UserSession
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RefreshTokenHash = UserSessionService.HashRefreshToken(refreshToken),
                CreatedAt = now,
                ExpiresAt = refreshExpiresAt.Value,
                LastSeenAt = now,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                DeviceName = deviceName,
                Platform = platform,
                ClientVersion = clientVersion,
                IsTrusted = false
            };
            db.UserSessions.Add(session);
        }

        await db.SaveChangesAsync(ct);

        await loginNotifications.TrySendLoginNotificationAsync(
            user,
            new LoginNotificationContext(now, ipAddress, deviceName, platform, clientVersion, userAgent, knownDevice, UsedTwoFactor: false, UsedRecoveryCode: false, SessionId: session?.Id),
            HttpContext,
            ct);

        var accessExpiresAt = now.AddMinutes(60);
        var token = tokens.Create(user, roles, session?.Id, accessExpiresAt);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.LoginSucceeded", "User", user.Id.ToString(), $"Login succeeded. rememberMe={r.RememberMe}; sessionId={session?.Id.ToString() ?? "none"}; knownDevice={knownDevice}; publicId={user.PublicId}", ct);
        return new TokenResponse(
            token,
            ToUserDto(user),
            roles.Select(role => role.ToString()).ToList(),
            refreshToken,
            session?.Id,
            accessExpiresAt,
            refreshExpiresAt);
    }


    [HttpPost("login/2fa")]
    public async Task<ActionResult<TokenResponse>> LoginWithTwoFactor([FromBody] TwoFactorLoginRequest r, CancellationToken ct)
    {
        var tokenValue = (r.ChallengeToken ?? string.Empty).Trim();
        var code = (r.Code ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tokenValue) || string.IsNullOrWhiteSpace(code))
            return Unauthorized("Введите код двухфакторной защиты.");

        var tokenHash = TotpService.HashLoginChallengeToken(tokenValue);
        var now = DateTime.UtcNow;
        var challenge = await db.TwoFactorLoginChallenges
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.ChallengeTokenHash == tokenHash, ct);

        if (challenge is null || challenge.User is null || challenge.UsedAt != null || challenge.ExpiresAt <= now)
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.Login2FAFailed", "2FA", SecurityAuditService.HashTarget(tokenValue), "2FA challenge invalid or expired.", ct);
            return Unauthorized("Код входа истёк. Войдите заново.");
        }

        var user = challenge.User;
        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TwoFactorSecretEncrypted) || user.IsDisabled)
            return Unauthorized("Двухфакторная защита недоступна.");

        var normalizedOtp = TotpService.NormalizeCode(code);
        var verified = false;
        var usedRecoveryCode = false;
        var usedEmailCode = false;
        if (normalizedOtp.Length == 6 && normalizedOtp.All(char.IsDigit))
        {
            var secret = IdentityDataProtector.UnprotectSecret(user.TwoFactorSecretEncrypted);
            verified = TotpService.VerifyTotp(secret, normalizedOtp);
        }

        if (!verified)
        {
            var normalizedRecovery = TotpService.NormalizeRecoveryCode(code);
            if (normalizedRecovery.Length >= 8)
            {
                var recoveryHash = TotpService.HashRecoveryCode(user.Id, normalizedRecovery);
                var recovery = await db.TwoFactorRecoveryCodes
                    .FirstOrDefaultAsync(c => c.UserId == user.Id && c.CodeHash == recoveryHash && c.UsedAt == null, ct);
                if (recovery is not null)
                {
                    recovery.UsedAt = now;
                    recovery.UsedIpAddress = UserSessionService.GetRemoteIp(HttpContext);
                    verified = true;
                    usedRecoveryCode = true;
                }
            }
        }

        if (!verified)
        {
            if (await emailVerification.TryConsumeTwoFactorLoginCodeAsync(user.Id, code, HttpContext, challenge.Id, ct))
            {
                verified = true;
                usedEmailCode = true;
            }
        }

        if (!verified)
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.Login2FAFailed", "User", user.Id.ToString(), $"Wrong 2FA/recovery/email code. publicId={user.PublicId}", ct);
            return Unauthorized("Неверный код из приложения-аутентификатора, recovery-код или код с почты.");
        }

        challenge.UsedAt = now;
        user.UpdatedAt = now;

        var roles = await db.UserRoles
            .Where(role => role.UserId == user.Id && role.RevokedAt == null)
            .Select(role => role.Role)
            .ToListAsync(ct);

        var ipAddress = UserSessionService.GetRemoteIp(HttpContext);
        var userAgent = UserSessionService.CleanHeader(Request.Headers.UserAgent.ToString(), 256);
        var deviceName = UserSessionService.CleanHeader(challenge.DeviceName, 128);
        var platform = UserSessionService.CleanHeader(challenge.Platform, 64);
        var clientVersion = UserSessionService.CleanHeader(challenge.ClientVersion, 32);
        var knownDevice = await IsKnownLoginDeviceAsync(user.Id, deviceName, platform, clientVersion, ct);

        UserSession? session = null;
        string? refreshToken = null;
        DateTime? refreshExpiresAt = null;

        if (challenge.RememberMe)
        {
            refreshToken = UserSessionService.NewRefreshToken();
            refreshExpiresAt = now.AddDays(30);
            session = new UserSession
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RefreshTokenHash = UserSessionService.HashRefreshToken(refreshToken),
                CreatedAt = now,
                ExpiresAt = refreshExpiresAt.Value,
                LastSeenAt = now,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                DeviceName = deviceName,
                Platform = platform,
                ClientVersion = clientVersion,
                IsTrusted = false
            };
            db.UserSessions.Add(session);
        }

        await db.SaveChangesAsync(ct);

        await loginNotifications.TrySendLoginNotificationAsync(
            user,
            new LoginNotificationContext(now, ipAddress, deviceName, platform, clientVersion, userAgent, knownDevice, UsedTwoFactor: true, UsedRecoveryCode: usedRecoveryCode, SessionId: session?.Id, UsedEmailCode: usedEmailCode),
            HttpContext,
            ct);

        var accessExpiresAt = now.AddMinutes(60);
        var token = tokens.Create(user, roles, session?.Id, accessExpiresAt);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.Login2FASucceeded", "User", user.Id.ToString(), $"2FA login succeeded. rememberMe={challenge.RememberMe}; recoveryCode={usedRecoveryCode}; emailCode={usedEmailCode}; sessionId={session?.Id.ToString() ?? "none"}; knownDevice={knownDevice}; publicId={user.PublicId}", ct);
        return new TokenResponse(
            token,
            ToUserDto(user),
            roles.Select(role => role.ToString()).ToList(),
            refreshToken,
            session?.Id,
            accessExpiresAt,
            refreshExpiresAt);
    }


    [HttpPost("login/2fa/email-code/send")]
    public async Task<ActionResult<TwoFactorEmailCodeResponse>> SendTwoFactorEmailCode([FromBody] TwoFactorEmailCodeRequest r, CancellationToken ct)
    {
        var tokenValue = (r.ChallengeToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tokenValue))
            return Unauthorized("Код входа истёк. Войдите заново.");

        var tokenHash = TotpService.HashLoginChallengeToken(tokenValue);
        var now = DateTime.UtcNow;
        var challenge = await db.TwoFactorLoginChallenges
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.ChallengeTokenHash == tokenHash, ct);

        if (challenge is null || challenge.User is null || challenge.UsedAt != null || challenge.ExpiresAt <= now)
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.Login2FAEmailCodeFailed", "2FA", SecurityAuditService.HashTarget(tokenValue), "2FA challenge invalid or expired for email code.", ct);
            return Unauthorized("Код входа истёк. Войдите заново.");
        }

        var user = challenge.User;
        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TwoFactorSecretEncrypted) || user.IsDisabled)
            return Unauthorized("Двухфакторная защита недоступна.");

        if (!user.EmailConfirmed)
            return BadRequest("Почта аккаунта не подтверждена. Используйте recovery-код 2FA.");

        var result = await emailVerification.SendTwoFactorLoginCodeAsync(user, HttpContext, challenge.Id, ct);
        await securityAudit.TryWriteAsync(User, HttpContext, result.Sent ? "Security.Login2FAEmailCodeSent" : "Security.Login2FAEmailCodeNotSent", "User", user.Id.ToString(), result.Message, ct);

        if (result.ExpiresAt.HasValue && challenge.ExpiresAt < result.ExpiresAt.Value)
        {
            challenge.ExpiresAt = result.ExpiresAt.Value;
            await db.SaveChangesAsync(ct);
        }

        var response = new TwoFactorEmailCodeResponse(
            result.Sent,
            result.Cooldown,
            result.RetryAfterSeconds,
            result.ExpiresAt,
            result.Message);

        if (result.Cooldown)
            return StatusCode(StatusCodes.Status429TooManyRequests, response);

        if (!result.Sent)
            return BadRequest(response);

        return Ok(response);
    }


    [AllowAnonymous]
    [HttpGet("login-alerts/{token}/not-me")]
    public async Task<IActionResult> MarkLoginAsSuspicious([FromRoute] string token, CancellationToken ct)
    {
        var result = await loginNotifications.HandleNotMeAsync(token, HttpContext, ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.LoginMarkedSuspicious", "LoginAlert", SecurityAuditService.HashTarget(token ?? string.Empty), $"state={result.State}; revokedSessions={result.RevokedSessions}; publicId={result.PublicId}", ct);
        return Content(LoginNotificationService.BuildResultHtml(result), "text/html; charset=utf-8");
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<TokenResponse>> Refresh([FromBody] RefreshTokenRequest r, CancellationToken ct)
    {
        var refreshToken = (r.RefreshToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.RefreshFailed", "Session", "empty", "Refresh token missing.", ct);
            return Unauthorized("Refresh token отсутствует.");
        }

        var tokenHash = UserSessionService.HashRefreshToken(refreshToken);
        var now = DateTime.UtcNow;

        var session = await db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == tokenHash, ct);

        if (session is null || !UserSessionService.IsActive(session, now) || session.User is null)
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.RefreshFailed", "Session", SecurityAuditService.HashTarget(refreshToken), "Refresh token invalid, revoked or expired.", ct);
            return Unauthorized("Сессия недействительна.");
        }

        var user = session.User;
        if (user.IsDisabled)
        {
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.RefreshBlockedDisabled", "User", user.Id.ToString(), $"Refresh blocked for disabled account. publicId={user.PublicId}; sessionId={session.Id}", ct);
            return StatusCode(StatusCodes.Status403Forbidden, string.IsNullOrWhiteSpace(user.DisabledReason)
                ? "Аккаунт отключён."
                : user.DisabledReason);
        }

        var roles = await db.UserRoles
            .Where(role => role.UserId == user.Id && role.RevokedAt == null)
            .Select(role => role.Role)
            .ToListAsync(ct);

        var newRefreshToken = UserSessionService.NewRefreshToken();
        session.RefreshTokenHash = UserSessionService.HashRefreshToken(newRefreshToken);
        session.LastRefreshAt = now;
        session.LastSeenAt = now;
        session.ExpiresAt = now.AddDays(30);
        session.IpAddress = UserSessionService.GetRemoteIp(HttpContext);
        session.UserAgent = UserSessionService.CleanHeader(Request.Headers.UserAgent.ToString(), 256);
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        var accessExpiresAt = now.AddMinutes(60);
        var token = tokens.Create(user, roles, session.Id, accessExpiresAt);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.RefreshSucceeded", "Session", session.Id.ToString(), $"Refresh succeeded. publicId={user.PublicId}", ct);
        return new TokenResponse(
            token,
            ToUserDto(user),
            roles.Select(role => role.ToString()).ToList(),
            newRefreshToken,
            session.Id,
            accessExpiresAt,
            session.ExpiresAt);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? r, CancellationToken ct)
    {
        var uid = GetCurrentUserId();
        var refreshToken = (r?.RefreshToken ?? string.Empty).Trim();
        var sid = GetCurrentSessionId();

        IQueryable<UserSession> query = db.UserSessions.Where(s => s.UserId == uid && s.RevokedAt == null);
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var tokenHash = UserSessionService.HashRefreshToken(refreshToken);
            query = query.Where(s => s.RefreshTokenHash == tokenHash);
        }
        else if (sid.HasValue)
        {
            query = query.Where(s => s.Id == sid.Value);
        }
        else
        {
            return NoContent();
        }

        var session = await query.FirstOrDefaultAsync(ct);
        if (session != null)
        {
            session.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await securityAudit.TryWriteAsync(User, HttpContext, "Security.Logout", "Session", session.Id.ToString(), "Session revoked by logout.", ct);
        }

        return NoContent();
    }

    [Authorize]
    [HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        var uid = GetCurrentUserId();
        var currentSid = GetCurrentSessionId();
        var now = DateTime.UtcNow;

        var sessions = await db.UserSessions
            .Where(s => s.UserId == uid && s.RevokedAt == null && (!currentSid.HasValue || s.Id != currentSid.Value))
            .ToListAsync(ct);

        foreach (var session in sessions)
            session.RevokedAt = now;

        await db.SaveChangesAsync(ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.LogoutAll", "Session", currentSid?.ToString() ?? "all", $"Revoked other sessions count={sessions.Count}.", ct);
        return NoContent();
    }

    [Authorize]
    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<UserSessionDto>>> Sessions(CancellationToken ct)
    {
        var uid = GetCurrentUserId();
        var currentSid = GetCurrentSessionId();
        var now = DateTime.UtcNow;

        var sessions = await db.UserSessions
            .Where(s => s.UserId == uid && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt ?? s.CreatedAt)
            .Select(s => new UserSessionDto(
                s.Id,
                s.CreatedAt,
                s.ExpiresAt,
                s.LastSeenAt,
                s.LastRefreshAt,
                s.DeviceName,
                s.Platform,
                s.ClientVersion,
                s.IpAddress,
                currentSid.HasValue && s.Id == currentSid.Value))
            .ToListAsync(ct);

        return sessions;
    }


    [Authorize]
    [HttpGet("login-history")]
    public async Task<ActionResult<IReadOnlyList<UserLoginHistoryDto>>> LoginHistory(CancellationToken ct)
    {
        var uid = GetCurrentUserId();
        var currentSid = GetCurrentSessionId();
        var now = DateTime.UtcNow;

        var history = await db.UserSessions
            .Where(s => s.UserId == uid)
            .OrderByDescending(s => s.LastSeenAt ?? s.LastRefreshAt ?? s.CreatedAt)
            .Take(40)
            .Select(s => new UserLoginHistoryDto(
                s.Id,
                s.CreatedAt,
                s.ExpiresAt,
                s.LastSeenAt,
                s.LastRefreshAt,
                s.RevokedAt,
                s.DeviceName,
                s.Platform,
                s.ClientVersion,
                s.IpAddress,
                currentSid.HasValue && s.Id == currentSid.Value,
                s.RevokedAt == null && s.ExpiresAt > now))
            .ToListAsync(ct);

        return history;
    }

    [Authorize]
    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken ct)
    {
        var uid = GetCurrentUserId();
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == uid && s.RevokedAt == null, ct);
        if (session is null)
            return NotFound();

        session.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.SessionRevoked", "Session", session.Id.ToString(), "Session revoked from account settings.", ct);
        return NoContent();
    }


    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        var id = GetCurrentUserId();
        var u = await db.Users.FindAsync(new object?[] { id }, ct);
        if (u is null) return NotFound();
        return ToUserDto(u);
    }

    private Guid GetCurrentUserId()
    {
        if (TryGetCurrentUserId(out var id))
            return id;

        throw new InvalidOperationException("Current user id claim is missing or invalid.");
    }

    private bool TryGetCurrentUserId(out Guid id)
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirstValue("sub")
                    ?? User.FindFirstValue("nameid");

        return Guid.TryParse(idStr, out id);
    }

    private Guid? GetCurrentSessionId()
    {
        var sid = User.FindFirstValue("sid");
        return Guid.TryParse(sid, out var id) ? id : null;
    }


    private async Task<bool> IsKnownLoginDeviceAsync(Guid userId, string deviceName, string platform, string clientVersion, CancellationToken ct)
    {
        deviceName = UserSessionService.CleanHeader(deviceName, 128);
        platform = UserSessionService.CleanHeader(platform, 64);
        clientVersion = UserSessionService.CleanHeader(clientVersion, 32);
        var now = DateTime.UtcNow;

        return await db.UserSessions.AnyAsync(s =>
            s.UserId == userId &&
            s.RevokedAt == null &&
            s.ExpiresAt > now &&
            s.DeviceName == deviceName &&
            s.Platform == platform &&
            s.ClientVersion == clientVersion,
            ct);
    }

    private static UserDto ToUserDto(User u) => new(
        u.Id,
        UserIdentityService.GetPublicName(u),
        UserIdentityService.GetEmail(u),
        u.CreatedAt,
        UserIdentityService.GetLogin(u),
        UserIdentityService.GetPublicName(u),
        u.PublicId,
        u.EmailConfirmed,
        u.EmailVerifiedAt,
        UserIdentityService.GetAvatarUrl(u));
}
