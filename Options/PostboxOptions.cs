namespace JaeZoo.Server.Options;

public sealed class PostboxOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "postbox.cloud.yandex.net";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = "noreply@jaezoo.ru";
    public string FromName { get; set; } = "JaeZoo";
    public int CodeLifetimeMinutes { get; set; } = 15;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
}
