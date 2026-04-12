namespace JaeZoo.Server.Options;

public sealed class LauncherUpdatesOptions
{
    public string Bucket { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "https://storage.yandexcloud.net";
    public string Region { get; set; } = "ru-central1";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    public string Channel { get; set; } = "stable";
    public int SignedUrlTtlMinutes { get; set; } = 15;

    // Optional overrides. Leave empty to use the default layout:
    // {channel}/client/manifest.json and {channel}/launcher/manifest.json
    public string? ClientManifestKey { get; set; }
    public string? LauncherManifestKey { get; set; }

    // CDN package delivery for setup/bootstrap.
    public string CdnBaseUrl { get; set; } = string.Empty;
    public string CdnSecureKey { get; set; } = string.Empty;
    public string? PackageKey { get; set; }
    public int PackageUrlTtlSeconds { get; set; } = 300;
}
