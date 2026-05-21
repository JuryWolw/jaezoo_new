using System.Net;
using System.Net.Mail;
using System.Text;
using JaeZoo.Server.Models;
using JaeZoo.Server.Options;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Email;

public sealed class PostboxEmailSender(IOptions<PostboxOptions> options, ILogger<PostboxEmailSender> logger) : IEmailSender
{
    private readonly PostboxOptions _options = options.Value;

    public async Task SendEmailConfirmationCodeAsync(User user, string code, CancellationToken ct)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Postbox is disabled. Set Postbox:Enabled=true.");

        if (string.IsNullOrWhiteSpace(_options.UserName) || string.IsNullOrWhiteSpace(_options.Password))
            throw new InvalidOperationException("Postbox SMTP credentials are not configured.");

        if (string.IsNullOrWhiteSpace(_options.FromEmail))
            throw new InvalidOperationException("Postbox:FromEmail is not configured.");

        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? "JaeZoo user" : user.DisplayName;
        var subject = "Код подтверждения JaeZoo";
        var text = $"""
Привет, {displayName}!

Код подтверждения аккаунта JaeZoo: {code}

Код действует 15 минут. Если это были не вы, просто проигнорируйте письмо.
""";

        var html = $"""
<!doctype html>
<html lang="ru">
<head><meta charset="utf-8"></head>
<body style="font-family: Arial, sans-serif; color: #1f1f1f;">
  <h2>Подтверждение аккаунта JaeZoo</h2>
  <p>Привет, {WebUtility.HtmlEncode(displayName)}!</p>
  <p>Ваш код подтверждения:</p>
  <div style="font-size: 28px; font-weight: 700; letter-spacing: 6px; padding: 14px 18px; background: #f2f2f2; border-radius: 10px; display: inline-block;">{WebUtility.HtmlEncode(code)}</div>
  <p>Код действует 15 минут.</p>
  <p style="color:#777; font-size: 13px;">Если это были не вы, просто проигнорируйте письмо.</p>
</body>
</html>
""";

        using var msg = new MailMessage
        {
            From = new MailAddress(_options.FromEmail, _options.FromName, Encoding.UTF8),
            Subject = subject,
            SubjectEncoding = Encoding.UTF8,
            Body = html,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = true
        };
        msg.To.Add(new MailAddress(user.Email));
        msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(text, Encoding.UTF8, "text/plain"));

        using var smtp = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            Credentials = new NetworkCredential(_options.UserName, _options.Password),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 60000
        };

        using var reg = ct.Register(() =>
        {
            try { smtp.SendAsyncCancel(); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to cancel SMTP send."); }
        });

        await smtp.SendMailAsync(msg);
    }
}
