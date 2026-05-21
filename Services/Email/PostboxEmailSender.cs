using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

        var transport = string.IsNullOrWhiteSpace(_options.Transport) ? "Http" : _options.Transport.Trim();
        return transport.Equals("Smtp", StringComparison.OrdinalIgnoreCase)
            ? SendViaSmtpAsync(user, code, ct)
            : SendViaHttpApiAsync(user, code, ct);
    }

    private async Task SendViaHttpApiAsync(User user, string code, CancellationToken ct)
    {
        var (subject, text, html) = BuildMessage(user, code);

        var payload = new
        {
            FromEmailAddress = _options.FromEmail,
            Destination = new
            {
                ToAddresses = new[] { user.Email }
            },
            Content = new
            {
                Simple = new
                {
                    Subject = new
                    {
                        Data = subject,
                        Charset = "UTF-8"
                    },
                    Body = new
                    {
                        Text = new
                        {
                            Data = text,
                            Charset = "UTF-8"
                        },
                        Html = new
                        {
                            Data = html,
                            Charset = "UTF-8"
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var endpoint = new Uri(_options.ApiEndpoint);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.SendTimeoutSeconds, 5, 90));

        using var http = new HttpClient { Timeout = timeout };
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        SignAwsV4(req, endpoint, json, _options.UserName, _options.Password, _options.Region, _options.Service);

        logger.LogInformation(
            "Sending email confirmation via Yandex Postbox HTTP API. Endpoint={Endpoint} From={From} To={To} KeyId={KeyId}",
            endpoint.GetLeftPart(UriPartial.Authority), _options.FromEmail, user.Email, MaskKey(_options.UserName));

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.IsSuccessStatusCode)
        {
            logger.LogInformation("Yandex Postbox HTTP API accepted email. StatusCode={StatusCode}", (int)res.StatusCode);
            return;
        }

        var body = await res.Content.ReadAsStringAsync(ct);
        if (body.Length > 1200) body = body[..1200];

        throw new InvalidOperationException(
            $"Yandex Postbox HTTP API failed: HTTP {(int)res.StatusCode} {res.ReasonPhrase}. {body}");
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
            "Sending email confirmation via Yandex Postbox SMTP. Host={Host} Port={Port} From={From} To={To} KeyId={KeyId}",
            _options.Host, _options.Port, _options.FromEmail, user.Email, MaskKey(_options.UserName));

        await smtp.SendMailAsync(msg);
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

    private static void SignAwsV4(HttpRequestMessage req, Uri endpoint, string payload, string accessKeyId, string secretKey, string region, string service)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var host = endpoint.Host;
        var canonicalUri = string.IsNullOrWhiteSpace(endpoint.AbsolutePath) ? "/" : endpoint.AbsolutePath;
        var canonicalQuery = endpoint.Query.TrimStart('?');
        var payloadHash = Sha256Hex(payload);

        const string signedHeaders = "content-type;host;x-amz-date";
        var canonicalHeaders = $"content-type:application/json; charset=utf-8\nhost:{host}\nx-amz-date:{amzDate}\n";
        var canonicalRequest = $"POST\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";
        var signingKey = GetSignatureKey(secretKey, dateStamp, region, service);
        var signature = ToHex(HmacSha256(signingKey, stringToSign));

        req.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(
            $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static byte[] GetSignatureKey(string secretKey, string dateStamp, string regionName, string serviceName)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = HmacSha256(kDate, regionName);
        var kService = HmacSha256(kRegion, serviceName);
        return HmacSha256(kService, "aws4_request");
    }

    private static byte[] HmacSha256(byte[] key, string data)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Sha256Hex(string data)
        => ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(data)));

    private static string ToHex(byte[] bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string MaskKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<empty>";
        value = value.Trim();
        return value.Length <= 8 ? "***" : $"{value[..4]}…{value[^4..]}";
    }
}
