using System.Collections.Concurrent;
using System.Security.Claims;

namespace JaeZoo.Server.Services.Security;

public sealed class RiskCaptchaService
{
    private readonly ConcurrentDictionary<string, RiskWindow> _windows = new(StringComparer.Ordinal);
    private readonly SmartCaptchaService _captcha;
    private readonly ILogger<RiskCaptchaService> _log;

    public RiskCaptchaService(SmartCaptchaService captcha, ILogger<RiskCaptchaService> log)
    {
        _captcha = captcha;
        _log = log;
    }

    public async Task<RiskCaptchaCheckResult> CheckAsync(
        HttpContext httpContext,
        string action,
        int maxActions,
        TimeSpan window,
        CancellationToken ct)
    {
        var key = BuildKey(httpContext, action);
        var now = DateTimeOffset.UtcNow;
        var state = _windows.AddOrUpdate(
            key,
            _ => new RiskWindow(now, 1),
            (_, old) =>
            {
                if (now - old.StartedAt >= window)
                    return new RiskWindow(now, 1);

                return old with { Count = old.Count + 1 };
            });

        if (state.Count <= maxActions)
            return RiskCaptchaCheckResult.Allowed();

        var token = httpContext.Request.Headers["X-JaeZoo-Captcha-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogWarning("Risk captcha required. Action={Action}; Key={Key}; Count={Count}; Limit={Limit}", action, key, state.Count, maxActions);
            return RiskCaptchaCheckResult.Required("Система обнаружила подозрительную активность. Подтвердите, что вы человек.");
        }

        var captchaResult = await _captcha.ValidateAsync(token, httpContext, ct);
        if (!captchaResult.Success)
            return RiskCaptchaCheckResult.Required(captchaResult.Message);

        _windows[key] = new RiskWindow(now, 0);
        _log.LogInformation("Risk captcha passed. Action={Action}; Key={Key}", action, key);
        return RiskCaptchaCheckResult.Allowed();
    }

    private static string BuildKey(HttpContext httpContext, string action)
    {
        var userId = httpContext.User.FindFirst("sub")?.Value
                     ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? httpContext.User.FindFirst("uid")?.Value;

        if (!string.IsNullOrWhiteSpace(userId))
            return $"user:{userId}:{action}";

        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}:{action}";
    }

    private readonly record struct RiskWindow(DateTimeOffset StartedAt, int Count);
}

public readonly record struct RiskCaptchaCheckResult(bool Success, string Message)
{
    public static RiskCaptchaCheckResult Allowed() => new(true, string.Empty);
    public static RiskCaptchaCheckResult Required(string message) => new(false, message);
}
