using System.Net;
using System.Security.Cryptography;
using System.Text;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Security;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Security;

public sealed record LoginNotificationContext(
    DateTime LoginAtUtc,
    string IpAddress,
    string DeviceName,
    string Platform,
    string ClientVersion,
    string UserAgent,
    bool IsKnownDevice,
    bool UsedTwoFactor,
    bool UsedRecoveryCode,
    Guid? SessionId);

public sealed class LoginNotificationService(
    AppDbContext db,
    IEmailSender emailSender,
    ILogger<LoginNotificationService> logger)
{
    private static readonly TimeSpan AlertLifetime = TimeSpan.FromDays(7);

    public async Task TrySendLoginNotificationAsync(User user, LoginNotificationContext context, HttpContext httpContext, CancellationToken ct)
    {
        try
        {
            var rawToken = NewAlertToken();
            var alert = new LoginAlertToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                SessionId = context.SessionId,
                TokenHash = HashAlertToken(rawToken),
                IpAddress = Clean(context.IpAddress, 64),
                DeviceName = Clean(context.DeviceName, 128),
                Platform = Clean(context.Platform, 64),
                ClientVersion = Clean(context.ClientVersion, 32),
                IsKnownDevice = context.IsKnownDevice,
                UsedTwoFactor = context.UsedTwoFactor,
                UsedRecoveryCode = context.UsedRecoveryCode,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(AlertLifetime)
            };

            db.LoginAlertTokens.Add(alert);
            await db.SaveChangesAsync(ct);

            var (subject, text, html) = BuildLoginEmail(user, context, BuildNotMeUrl(httpContext, rawToken));
            await emailSender.SendAccountNotificationAsync(user, subject, text, html, ct);

            logger.LogInformation(
                "Login email notification sent. UserId={UserId} PublicId={PublicId} SessionId={SessionId} KnownDevice={KnownDevice} Ip={Ip}",
                user.Id,
                user.PublicId,
                context.SessionId,
                context.IsKnownDevice,
                MaskIp(context.IpAddress));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Login email notification failed. UserId={UserId} PublicId={PublicId} Ip={Ip}",
                user.Id,
                user.PublicId,
                MaskIp(context.IpAddress));
        }
    }

    public async Task<LoginAlertHandleResult> HandleNotMeAsync(string token, HttpContext httpContext, CancellationToken ct)
    {
        var normalized = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return LoginAlertHandleResult.Invalid;

        var hash = HashAlertToken(normalized);
        var now = DateTime.UtcNow;
        var alert = await db.LoginAlertTokens
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.TokenHash == hash, ct);

        if (alert is null || alert.User is null)
            return LoginAlertHandleResult.Invalid;

        if (alert.UsedAt != null)
            return LoginAlertHandleResult.AlreadyUsed;

        if (alert.ExpiresAt <= now)
            return LoginAlertHandleResult.Expired;

        var sessions = await db.UserSessions
            .Where(s => s.UserId == alert.UserId && s.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var session in sessions)
            session.RevokedAt = now;

        alert.UsedAt = now;
        alert.UsedIpAddress = Clean(UserSessionService.GetRemoteIp(httpContext), 64);

        alert.User.TokenVersion += 1;
        alert.User.SecurityStamp = UserIdentityService.NewSecurityStamp();
        alert.User.UpdatedAt = now;

        await db.SaveChangesAsync(ct);

        try
        {
            const string subject = "JaeZoo: вход отмечен как подозрительный";
            var text = $"""
Вы отметили вход в аккаунт JaeZoo как подозрительный.

Что сделано автоматически:
- активные сессии аккаунта завершены;
- старые токены доступа отозваны;
- для продолжения работы потребуется новый вход.

Если вы не нажимали эту ссылку, войдите в аккаунт и смените пароль.
""";
            await emailSender.SendAccountNotificationAsync(alert.User, subject, text, null, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send suspicious-login follow-up email. UserId={UserId}", alert.UserId);
        }

        return new LoginAlertHandleResult(true, sessions.Count, alert.User.PublicId ?? string.Empty);
    }

    public static string HashAlertToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string BuildResultHtml(LoginAlertHandleResult result)
    {
        var title = result switch
        {
            { Handled: true } => "Вход отмечен как подозрительный",
            { State: LoginAlertHandleState.AlreadyUsed } => "Ссылка уже использована",
            { State: LoginAlertHandleState.Expired } => "Ссылка устарела",
            _ => "Ссылка недействительна"
        };

        var body = result switch
        {
            { Handled: true } => $"Активные сессии завершены: {result.RevokedSessions}. Войдите в JaeZoo заново и смените пароль.",
            { State: LoginAlertHandleState.AlreadyUsed } => "Эта ссылка уже была использована. Если проблема сохраняется, смените пароль в настройках.",
            { State: LoginAlertHandleState.Expired } => "Срок действия ссылки истек. Если вход был подозрительным, войдите в аккаунт и завершите все сессии вручную.",
            _ => "Мы не смогли проверить эту ссылку. Откройте настройки безопасности JaeZoo и проверьте активные сессии."
        };

        return $"""
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>{WebUtility.HtmlEncode(title)}</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
</head>
<body style="margin:0;background:#10131c;color:#eef2ff;font-family:Arial,sans-serif;">
  <main style="max-width:680px;margin:54px auto;padding:28px;border-radius:22px;background:#171b28;border:1px solid rgba(255,255,255,.08);box-shadow:0 24px 70px rgba(0,0,0,.35);">
    <div style="font-size:13px;color:#8aa0ff;text-transform:uppercase;letter-spacing:.12em;margin-bottom:10px;">JaeZoo Security</div>
    <h1 style="font-size:28px;margin:0 0 14px;">{WebUtility.HtmlEncode(title)}</h1>
    <p style="font-size:16px;line-height:1.6;color:#cfd6ea;margin:0;">{WebUtility.HtmlEncode(body)}</p>
  </main>
</body>
</html>
""";
    }

    private static (string Subject, string Text, string Html) BuildLoginEmail(User user, LoginNotificationContext context, string notMeUrl)
    {
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? "пользователь JaeZoo" : user.DisplayName!;
        var login = Safe(UserIdentityService.GetLogin(user));
        var publicId = Safe(user.PublicId);
        var utc = context.LoginAtUtc.ToUniversalTime();
        var moscow = utc.AddHours(3);
        var device = string.IsNullOrWhiteSpace(context.DeviceName) ? "не указано" : context.DeviceName;
        var platform = string.IsNullOrWhiteSpace(context.Platform) ? "не указано" : context.Platform;
        var clientVersion = string.IsNullOrWhiteSpace(context.ClientVersion) ? "не указано" : context.ClientVersion;
        var deviceType = context.IsKnownDevice ? "знакомое устройство" : "новое устройство";
        var protection = context.UsedTwoFactor
            ? context.UsedRecoveryCode ? "пароль + recovery-код 2FA" : "пароль + код 2FA"
            : "пароль";

        var subject = context.IsKnownDevice
            ? "JaeZoo: вход в аккаунт"
            : "JaeZoo: вход с нового устройства";

        var text = $"""
В ваш аккаунт JaeZoo выполнен вход.

Аккаунт: {displayName}
Логин: {login}
Public ID: {publicId}
Время: {FormatTime(utc)} UTC / {FormatTime(moscow)} МСК
IP: {context.IpAddress}
Устройство: {device}
Платформа: {platform}
Версия клиента: {clientVersion}
Тип: {deviceType}
Проверка входа: {protection}

Если это были вы, ничего делать не нужно.
Если это были не вы, откройте ссылку:
{notMeUrl}

Ссылка действует 7 дней. Она завершит активные сессии аккаунта.
""";

        var html = $"""
<!doctype html>
<html lang="ru">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"></head>
<body style="margin:0;background:#0f121b;color:#edf1ff;font-family:Arial,sans-serif;">
  <div style="max-width:720px;margin:0 auto;padding:34px 18px;">
    <div style="border-radius:24px;background:#171b28;border:1px solid rgba(255,255,255,.08);overflow:hidden;box-shadow:0 24px 70px rgba(0,0,0,.32);">
      <div style="padding:24px 28px;background:linear-gradient(135deg,#2432ff,#54a7ff);">
        <div style="font-size:13px;letter-spacing:.16em;text-transform:uppercase;color:rgba(255,255,255,.78);">JaeZoo Security</div>
        <h1 style="margin:10px 0 0;font-size:28px;color:#fff;">{WebUtility.HtmlEncode(subject)}</h1>
      </div>
      <div style="padding:26px 28px;">
        <p style="font-size:16px;line-height:1.6;color:#cfd6ea;margin:0 0 18px;">В ваш аккаунт выполнен вход. Проверьте данные ниже.</p>
        {Row("Аккаунт", displayName)}
        {Row("Логин", login)}
        {Row("Public ID", publicId)}
        {Row("Время", $"{FormatTime(utc)} UTC / {FormatTime(moscow)} МСК")}
        {Row("IP", context.IpAddress)}
        {Row("Устройство", device)}
        {Row("Платформа", platform)}
        {Row("Версия клиента", clientVersion)}
        {Row("Тип", deviceType)}
        {Row("Проверка входа", protection)}
        <div style="margin-top:24px;padding:18px;border-radius:18px;background:rgba(255,91,91,.10);border:1px solid rgba(255,91,91,.24);">
          <div style="font-weight:700;color:#ffd3d3;margin-bottom:8px;">Это были не вы?</div>
          <p style="font-size:14px;line-height:1.55;color:#e9c7c7;margin:0 0 14px;">Нажмите кнопку. JaeZoo завершит активные сессии аккаунта.</p>
          <a href="{WebUtility.HtmlEncode(notMeUrl)}" style="display:inline-block;background:#ff5b6b;color:white;text-decoration:none;font-weight:700;padding:12px 16px;border-radius:14px;">Это был не я</a>
        </div>
        <p style="font-size:12px;line-height:1.5;color:#8790a8;margin:18px 0 0;">Если это были вы, ничего делать не нужно. Ссылка действует 7 дней.</p>
      </div>
    </div>
  </div>
</body>
</html>
""";

        return (subject, text, html);
    }

    private static string Row(string label, string value)
    {
        return $"""
        <div style="display:flex;gap:16px;justify-content:space-between;align-items:flex-start;padding:12px 0;border-bottom:1px solid rgba(255,255,255,.07);">
          <div style="font-size:13px;color:#8790a8;min-width:130px;">{WebUtility.HtmlEncode(label)}</div>
          <div style="font-size:14px;color:#f0f4ff;text-align:right;word-break:break-word;">{WebUtility.HtmlEncode(value)}</div>
        </div>
""";
    }

    private static string BuildNotMeUrl(HttpContext httpContext, string rawToken)
    {
        var request = httpContext.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}".TrimEnd('/');
        return $"{baseUrl}/api/auth/login-alerts/{Uri.EscapeDataString(rawToken)}/not-me";
    }

    private static string NewAlertToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Clean(string? value, int max)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length <= max) return value;
        return value[..max];
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "не указано" : value.Trim();

    private static string FormatTime(DateTime value) => value.ToString("dd.MM.yyyy HH:mm:ss");

    private static string MaskIp(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var parts = value.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.*.*" : "***";
    }
}

public enum LoginAlertHandleState
{
    Handled,
    Invalid,
    Expired,
    AlreadyUsed
}

public sealed record LoginAlertHandleResult(bool Handled, int RevokedSessions, string PublicId, LoginAlertHandleState State)
{
    public static LoginAlertHandleResult Invalid { get; } = new(false, 0, string.Empty, LoginAlertHandleState.Invalid);
    public static LoginAlertHandleResult Expired { get; } = new(false, 0, string.Empty, LoginAlertHandleState.Expired);
    public static LoginAlertHandleResult AlreadyUsed { get; } = new(false, 0, string.Empty, LoginAlertHandleState.AlreadyUsed);

    public LoginAlertHandleResult(bool handled, int revokedSessions, string publicId)
        : this(handled, revokedSessions, publicId, handled ? LoginAlertHandleState.Handled : LoginAlertHandleState.Invalid)
    {
    }
}
