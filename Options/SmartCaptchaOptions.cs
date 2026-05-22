namespace JaeZoo.Server.Options;

public sealed class SmartCaptchaOptions
{
    public bool Enabled { get; set; } = false;
    public string ServerKey { get; set; } = string.Empty;
    public string ValidateEndpoint { get; set; } = "https://smartcaptcha.cloud.yandex.ru/validate";
    public int TimeoutSeconds { get; set; } = 10;
    public bool FailOpen { get; set; } = false;
}
