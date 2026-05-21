namespace JaeZoo.Server.Options;

public sealed class PostboxOptions
{
    public bool Enabled { get; set; }

    // Http = Yandex Cloud Postbox HTTPS API with AWS Signature V4.
    // Smtp = legacy SMTP mode. Render/PaaS often block SMTP ports, so Http is the safe default.
    public string Transport { get; set; } = "Http";

    public string Host { get; set; } = "postbox.cloud.yandex.net";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;

    // For Transport=Http: static access key ID / static secret access key.
    // For Transport=Smtp: API key ID / API key secret.
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public string FromEmail { get; set; } = "noreply@jaezoo.ru";
    public string FromName { get; set; } = "JaeZoo";

    public string ApiEndpoint { get; set; } = "https://postbox.cloud.yandex.net/v2/email/outbound-emails";
    public string Region { get; set; } = "ru-central1";
    public string Service { get; set; } = "ses";
    public int SendTimeoutSeconds { get; set; } = 30;

    public int CodeLifetimeMinutes { get; set; } = 15;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
}
