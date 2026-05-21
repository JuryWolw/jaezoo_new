namespace JaeZoo.Server.Options;

public sealed class PostboxOptions
{
    public bool Enabled { get; set; }

    // HttpSdk = Yandex Cloud Postbox HTTPS API through official AWS SESv2 SDK signing.
    // Smtp = legacy SMTP mode. Render/PaaS can block or stall SMTP ports, so HttpSdk is the safe default.
    public string Transport { get; set; } = "HttpSdk";

    public string Host { get; set; } = "postbox.cloud.yandex.net";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;

    // Transport=HttpSdk: static access key ID / static secret access key.
    // Transport=Smtp: API key ID / API key secret, or static key ID / generated SMTP password.
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public string FromEmail { get; set; } = "noreply@jaezoo.ru";
    public string FromName { get; set; } = "JaeZoo";

    // For HttpSdk use the root endpoint, without /v2/...
    public string ApiEndpoint { get; set; } = "https://postbox.cloud.yandex.net";
    public string Region { get; set; } = "ru-central1";
    public int SendTimeoutSeconds { get; set; } = 30;

    public int CodeLifetimeMinutes { get; set; } = 15;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
}
