namespace JaeZoo.Server.Options;

public sealed class LauncherUpdatesOptions
{
    public string Bucket { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "https://storage.yandexcloud.net";
    public string Region { get; set; } = "ru-central1";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    public string Channel { get; set; } = "stable";
    public string ManifestKey { get; set; } = "stable/manifest.json";
    public int SignedUrlTtlMinutes { get; set; } = 15;
}
