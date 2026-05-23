using System.Net;
using System.Net.Mail;
using System.Text;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using JaeZoo.Server.Models;
using JaeZoo.Server.Options;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Email;

public sealed class PostboxEmailSender(IOptions<PostboxOptions> options, ILogger<PostboxEmailSender> logger) : IEmailSender
{
    private readonly PostboxOptions _options = options.Value;

    public Task SendEmailConfirmationCodeAsync(User user, string code, CancellationToken ct)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Postbox is disabled. Set Postbox:Enabled=true.");

        if (string.IsNullOrWhiteSpace(_options.UserName) || string.IsNullOrWhiteSpace(_options.Password))
            throw new InvalidOperationException("Postbox credentials are not configured.");

        if (string.IsNullOrWhiteSpace(_options.FromEmail))
            throw new InvalidOperationException("Postbox:FromEmail is not configured.");

        var transport = string.IsNullOrWhiteSpace(_options.Transport) ? "HttpSdk" : _options.Transport.Trim();
        return transport.Equals("Smtp", StringComparison.OrdinalIgnoreCase)
            ? SendViaSmtpAsync(user, code, ct)
            : SendViaAwsSesV2SdkAsync(user, code, ct);
    }
    public Task SendAccountNotificationAsync(User user, string subject, string textBody, string? htmlBody, CancellationToken ct)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Postbox is disabled. Set Postbox:Enabled=true.");

        if (string.IsNullOrWhiteSpace(_options.UserName) || string.IsNullOrWhiteSpace(_options.Password))
            throw new InvalidOperationException("Postbox credentials are not configured.");

        if (string.IsNullOrWhiteSpace(_options.FromEmail))
            throw new InvalidOperationException("Postbox:FromEmail is not configured.");

        var transport = string.IsNullOrWhiteSpace(_options.Transport) ? "HttpSdk" : _options.Transport.Trim();
        return transport.Equals("Smtp", StringComparison.OrdinalIgnoreCase)
            ? SendNotificationViaSmtpAsync(user, subject, textBody, htmlBody, ct)
            : SendNotificationViaAwsSesV2SdkAsync(user, subject, textBody, htmlBody, ct);
    }


    private async Task SendViaAwsSesV2SdkAsync(User user, string code, CancellationToken ct)
    {
        var (subject, text, html) = BuildMessage(user, code);

        var endpoint = NormalizeServiceEndpoint(_options.ApiEndpoint);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.SendTimeoutSeconds, 5, 90));

        var credentials = new BasicAWSCredentials(_options.UserName.Trim(), _options.Password.Trim());
        var config = new AmazonSimpleEmailServiceV2Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = string.IsNullOrWhiteSpace(_options.Region) ? "ru-central1" : _options.Region.Trim(),
            Timeout = timeout,
            MaxErrorRetry = 0,
            UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        };

        using var client = new AmazonSimpleEmailServiceV2Client(credentials, config);

        var request = new SendEmailRequest
        {
            FromEmailAddress = _options.FromEmail,
            Destination = new Destination
            {
                ToAddresses = new List<string> { user.Email }
            },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content
                    {
                        Charset = "UTF-8",
                        Data = subject
                    },
                    Body = new Amazon.SimpleEmailV2.Model.Body
                    {
                        Text = new Content
                        {
                            Charset = "UTF-8",
                            Data = text
                        },
                        Html = new Content
                        {
                            Charset = "UTF-8",
                            Data = html
                        }
                    }
                }
            }
        };

        logger.LogInformation(
            "Sending email confirmation via Yandex Postbox HTTPS API. Endpoint={Endpoint} Region={Region} From={From} To={To} KeyId={KeyId} Timeout={TimeoutSeconds}s",
            endpoint,
            config.AuthenticationRegion,
            _options.FromEmail,
            user.Email,
            MaskKey(_options.UserName),
            timeout.TotalSeconds);

        try
        {
            var response = await client.SendEmailAsync(request, ct);
            logger.LogInformation(
                "Yandex Postbox HTTPS API accepted email. MessageId={MessageId} HttpStatusCode={StatusCode}",
                response.MessageId,
                (int)response.HttpStatusCode);
        }
        catch (AmazonServiceException ex)
        {
            logger.LogError(ex,
                "Yandex Postbox HTTPS API rejected email. StatusCode={StatusCode} ErrorCode={ErrorCode} RequestId={RequestId} Message={Message}",
                (int)ex.StatusCode,
                ex.ErrorCode,
                ex.RequestId,
                ex.Message);

            throw new InvalidOperationException(
                $"Yandex Postbox HTTPS API rejected email: HTTP {(int)ex.StatusCode}, {ex.ErrorCode}, {ex.Message}",
                ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex,
                "Yandex Postbox HTTPS API timed out after {TimeoutSeconds}s. Endpoint={Endpoint}",
                timeout.TotalSeconds,
                endpoint);

            throw new TimeoutException($"Yandex Postbox HTTPS API timed out after {timeout.TotalSeconds:0}s.", ex);
        }
    }

    private async Task SendViaSmtpAsync(User user, string code, CancellationToken ct)
    {
        var (subject, text, html) = BuildMessage(user, code);

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
            Timeout = Math.Clamp(_options.SendTimeoutSeconds, 5, 90) * 1000
        };

        using var reg = ct.Register(() =>
        {
            try { smtp.SendAsyncCancel(); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to cancel SMTP send."); }
        });

        logger.LogInformation(
            "Sending email confirmation via Yandex Postbox SMTP. Host={Host} Port={Port} From={From} To={To} KeyId={KeyId} Timeout={TimeoutSeconds}s",
            _options.Host,
            _options.Port,
            _options.FromEmail,
            user.Email,
            MaskKey(_options.UserName),
            Math.Clamp(_options.SendTimeoutSeconds, 5, 90));

        await smtp.SendMailAsync(msg);
    }

    private async Task SendNotificationViaAwsSesV2SdkAsync(User user, string subject, string textBody, string? htmlBody, CancellationToken ct)
    {
        var endpoint = NormalizeServiceEndpoint(_options.ApiEndpoint);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.SendTimeoutSeconds, 5, 90));

        var credentials = new BasicAWSCredentials(_options.UserName.Trim(), _options.Password.Trim());
        var config = new AmazonSimpleEmailServiceV2Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = string.IsNullOrWhiteSpace(_options.Region) ? "ru-central1" : _options.Region.Trim(),
            Timeout = timeout,
            MaxErrorRetry = 0,
            UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        };

        using var client = new AmazonSimpleEmailServiceV2Client(credentials, config);
        var html = string.IsNullOrWhiteSpace(htmlBody) ? BuildNotificationHtml(subject, textBody) : htmlBody!;
        var request = new SendEmailRequest
        {
            FromEmailAddress = _options.FromEmail,
            Destination = new Destination { ToAddresses = new List<string> { user.Email } },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Charset = "UTF-8", Data = subject },
                    Body = new Amazon.SimpleEmailV2.Model.Body
                    {
                        Text = new Content { Charset = "UTF-8", Data = textBody },
                        Html = new Content { Charset = "UTF-8", Data = html }
                    }
                }
            }
        };

        try
        {
            var response = await client.SendEmailAsync(request, ct);
            logger.LogInformation("Yandex Postbox accepted account notification. MessageId={MessageId} To={To}", response.MessageId, user.Email);
        }
        catch (AmazonServiceException ex)
        {
            logger.LogError(ex, "Yandex Postbox rejected account notification. StatusCode={StatusCode} ErrorCode={ErrorCode} RequestId={RequestId} Message={Message}", (int)ex.StatusCode, ex.ErrorCode, ex.RequestId, ex.Message);
            throw new InvalidOperationException($"Yandex Postbox rejected notification: HTTP {(int)ex.StatusCode}, {ex.ErrorCode}, {ex.Message}", ex);
        }
    }

    private async Task SendNotificationViaSmtpAsync(User user, string subject, string textBody, string? htmlBody, CancellationToken ct)
    {
        using var msg = new MailMessage
        {
            From = new MailAddress(_options.FromEmail, _options.FromName, Encoding.UTF8),
            Subject = subject,
            SubjectEncoding = Encoding.UTF8,
            Body = string.IsNullOrWhiteSpace(htmlBody) ? BuildNotificationHtml(subject, textBody) : htmlBody,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = true
        };
        msg.To.Add(new MailAddress(user.Email));
        msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, Encoding.UTF8, "text/plain"));

        using var smtp = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            Credentials = new NetworkCredential(_options.UserName, _options.Password),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = Math.Clamp(_options.SendTimeoutSeconds, 5, 90) * 1000
        };
        using var reg = ct.Register(() => { try { smtp.SendAsyncCancel(); } catch { } });
        await smtp.SendMailAsync(msg);
    }

    private static string BuildNotificationHtml(string subject, string textBody)
    {
        var htmlBody = WebUtility.HtmlEncode(textBody).Replace("\n", "<br>");
        return $"""
<!doctype html>
<html lang="ru">
<head><meta charset="utf-8"></head>
<body style="font-family: Arial, sans-serif; color: #1f1f1f;">
  <h2>{WebUtility.HtmlEncode(subject)}</h2>
  <p>{htmlBody}</p>
  <p style="color:#777; font-size:13px;">Это автоматическое уведомление JaeZoo.</p>
</body>
</html>
""";
    }

    private (string Subject, string Text, string Html) BuildMessage(User user, string code)
    {
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

        return (subject, text, html);
    }

    private static string NormalizeServiceEndpoint(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "https://postbox.cloud.yandex.net" : value.Trim();

        // AWS SDK itself appends the SESv2 operation path. If an old patch/env contains
        // /v2/email/outbound-emails, strip it to the service root.
        value = value.Replace("/v2/email/outbound-emails", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('/');

        return value;
    }

    private static string MaskKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<empty>";
        value = value.Trim();
        return value.Length <= 8 ? "***" : $"{value[..4]}…{value[^4..]}";
    }
}
