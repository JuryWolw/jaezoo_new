namespace JaeZoo.Server.Options;

public sealed class AdsOptions
{
    // Defaults to the LauncherUpdates bucket/credentials when left empty.
    public string Bucket { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    public string Prefix { get; set; } = "ads";
    public int SignedUrlTtlMinutes { get; set; } = 60;
    public long MaxImageBytes { get; set; } = 8 * 1024 * 1024;
}
