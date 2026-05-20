namespace JaeZoo.Server.Models.Ads;

public sealed class AdsManifest
{
    public int Version { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<AdsManifestItem> Items { get; set; } = new();
}

public sealed class AdsManifestItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ImageKey { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 0;
    public DateTime? StartAtUtc { get; set; }
    public DateTime? EndAtUtc { get; set; }
}

public sealed class AdBannerResponse
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public int Priority { get; set; }
}

public sealed class AdsResponse
{
    public int Version { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<AdBannerResponse> Items { get; set; } = new();
}

public sealed class AdminAdsManifestResponse
{
    public int Version { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<AdminAdBannerItem> Items { get; set; } = new();
}

public sealed class AdminAdBannerItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ImageKey { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public DateTime? StartAtUtc { get; set; }
    public DateTime? EndAtUtc { get; set; }
}

public sealed class SaveAdsManifestRequest
{
    public int Version { get; set; } = 1;
    public List<AdsManifestItem> Items { get; set; } = new();
}

public sealed class UploadAdImageResponse
{
    public string ImageKey { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
}
