using System.Text.Json;
using JaeZoo.Server.Options;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Security;

public sealed class SmartCaptchaService
{
    private readonly HttpClient _http;
    private readonly SmartCaptchaOptions _options;
    private readonly ILogger<SmartCaptchaService> _log;

    public SmartCaptchaService(HttpClient http, IOptions<SmartCaptchaOptions> options, ILogger<SmartCaptchaService> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;

        var timeout = Math.Clamp(_options.TimeoutSeconds, 3, 30);
        _http.Timeout = TimeSpan.FromSeconds(timeout);
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<SmartCaptchaValidationResult> ValidateAsync(string? token, HttpContext httpContext, CancellationToken ct)
    {
        if (!_options.Enabled)
            return SmartCaptchaValidationResult.Ok();

        if (string.IsNullOrWhiteSpace(_options.ServerKey))
        {
            _log.LogError("SmartCaptcha is enabled but SmartCaptcha:ServerKey is empty.");
            return _options.FailOpen
                ? SmartCaptchaValidationResult.Ok()
                : SmartCaptchaValidationResult.Failed("Проверка капчи временно недоступна.");
        }

        token = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
            return SmartCaptchaValidationResult.Failed("Подтвердите, что вы не робот.");

        var ip = GetClientIp(httpContext);
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = _options.ServerKey,
                ["token"] = token,
                ["ip"] = ip
            });

            using var response = await _http.PostAsync(_options.ValidateEndpoint, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("SmartCaptcha validation HTTP {Status}. Body={Body}", (int)response.StatusCode, Truncate(body, 500));
                return _options.FailOpen
                    ? SmartCaptchaValidationResult.Ok()
                    : SmartCaptchaValidationResult.Failed("Капча не прошла проверку. Попробуйте ещё раз.");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;

            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                return SmartCaptchaValidationResult.Ok();

            _log.LogWarning("SmartCaptcha rejected token. Status={Status}; Message={Message}", status, message);
            return SmartCaptchaValidationResult.Failed(string.IsNullOrWhiteSpace(message)
                ? "Капча не пройдена. Попробуйте ещё раз."
                : message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("SmartCaptcha validation timed out.");
            return _options.FailOpen
                ? SmartCaptchaValidationResult.Ok()
                : SmartCaptchaValidationResult.Failed("Капча не ответила вовремя. Попробуйте ещё раз.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SmartCaptcha validation failed.");
            return _options.FailOpen
                ? SmartCaptchaValidationResult.Ok()
                : SmartCaptchaValidationResult.Failed("Ошибка проверки капчи. Попробуйте позже.");
        }
    }

    private static string GetClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(first))
                return first;
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}

public readonly record struct SmartCaptchaValidationResult(bool Success, string Message)
{
    public static SmartCaptchaValidationResult Ok() => new(true, string.Empty);
    public static SmartCaptchaValidationResult Failed(string message) => new(false, message);
}
