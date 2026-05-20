using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using JaeZoo.Server.Models.Ads;
using JaeZoo.Server.Options;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;

namespace JaeZoo.Server.Services.Ads;

public sealed class AdsService : IAdsService
{
    private readonly AdsOptions _options;
    private readonly LauncherUpdatesOptions _launcherOptions;
    private readonly IAmazonS3 _s3;
    private readonly ILogger<AdsService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp"
    };

    public AdsService(
        IOptions<AdsOptions> options,
        IOptions<LauncherUpdatesOptions> launcherOptions,
        ILogger<AdsService> logger)
    {
        _options = options.Value;
        _launcherOptions = launcherOptions.Value;
        _logger = logger;

        var bucket = Bucket;
        var accessKey = AccessKey;
        var secretKey = SecretKey;

        if (string.IsNullOrWhiteSpace(bucket))
            throw new InvalidOperationException("Ads:Bucket or LauncherUpdates:Bucket is missing.");
        if (string.IsNullOrWhiteSpace(accessKey))
            throw new InvalidOperationException("Ads:AccessKey or LauncherUpdates:AccessKey is missing.");
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Ads:SecretKey or LauncherUpdates:SecretKey is missing.");

        var creds = new BasicAWSCredentials(accessKey, secretKey);
        var cfg = new AmazonS3Config
        {
            ServiceURL = Endpoint,
            AuthenticationRegion = Region,
            ForcePathStyle = true
        };

        _s3 = new AmazonS3Client(creds, cfg);
    }

    private string Bucket => FirstNotEmpty(_options.Bucket, _launcherOptions.Bucket);
    private string Endpoint => FirstNotEmpty(_options.Endpoint, _launcherOptions.Endpoint, "https://s3.yandexcloud.net");
    private string Region => FirstNotEmpty(_options.Region, _launcherOptions.Region, "ru-central1");
    private string AccessKey => FirstNotEmpty(_options.AccessKey, _launcherOptions.AccessKey);
    private string SecretKey => FirstNotEmpty(_options.SecretKey, _launcherOptions.SecretKey);
    private string Prefix => NormalizePrefix(_options.Prefix);
    private string ManifestKey => $"{Prefix}/manifest.json";
    private string ImagesPrefix => $"{Prefix}/images/";

    public async Task<AdsResponse> GetPublicAdsAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var items = new List<AdBannerResponse>();
        foreach (var item in manifest.Items)
        {
            if (!item.Enabled)
                continue;
            if (item.StartAtUtc is { } start && start > now)
                continue;
            if (item.EndAtUtc is { } end && end <= now)
                continue;
            if (string.IsNullOrWhiteSpace(item.ImageKey) || string.IsNullOrWhiteSpace(item.TargetUrl))
                continue;

            items.Add(new AdBannerResponse
            {
                Id = item.Id,
                Title = item.Title,
                ImageUrl = CreateSignedUrl(item.ImageKey),
                TargetUrl = item.TargetUrl,
                Priority = item.Priority
            });
        }

        return new AdsResponse
        {
            Version = manifest.Version,
            UpdatedAtUtc = manifest.UpdatedAtUtc,
            Items = items
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public async Task<AdminAdsManifestResponse> GetAdminManifestAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(cancellationToken);
        return ToAdminResponse(manifest);
    }

    public async Task<AdminAdsManifestResponse> SaveManifestAsync(SaveAdsManifestRequest request, CancellationToken cancellationToken = default)
    {
        var manifest = NormalizeAndValidate(request);

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = ManifestKey,
            InputStream = stream,
            ContentType = "application/json; charset=utf-8"
        }, cancellationToken);

        _logger.LogInformation("Ads manifest published to {Bucket}/{Key}: {Count} items", Bucket, ManifestKey, manifest.Items.Count);
        return ToAdminResponse(manifest);
    }

    public async Task<UploadAdImageResponse> UploadImageAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file.Length <= 0)
            throw new ArgumentException("Файл изображения пустой.");
        if (file.Length > Math.Max(1024, _options.MaxImageBytes))
            throw new ArgumentException($"Изображение слишком большое. Максимум: {Math.Max(1024, _options.MaxImageBytes) / 1024 / 1024} МБ.");

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedImageExtensions.Contains(ext))
            throw new ArgumentException("Поддерживаются только PNG, JPG, JPEG и WEBP.");

        var safeName = Slugify(Path.GetFileNameWithoutExtension(file.FileName));
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "banner";

        var key = $"{ImagesPrefix}{DateTime.UtcNow:yyyyMMddHHmmss}-{safeName}-{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var contentType = ContentTypes.TryGetValue(ext, out var ct) ? ct : "application/octet-stream";

        await using var input = file.OpenReadStream();
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = input,
            ContentType = contentType
        }, cancellationToken);

        return new UploadAdImageResponse
        {
            ImageKey = key,
            ImageUrl = CreateSignedUrl(key)
        };
    }

    public async Task DeleteImageAsync(string imageKey, CancellationToken cancellationToken = default)
    {
        var key = NormalizeImageKey(imageKey);
        if (string.IsNullOrWhiteSpace(key))
            return;

        await _s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = Bucket,
            Key = key
        }, cancellationToken);
    }

    private async Task<AdsManifest> LoadManifestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = Bucket,
                Key = ManifestKey
            }, cancellationToken);

            await using var stream = response.ResponseStream;
            var manifest = await JsonSerializer.DeserializeAsync<AdsManifest>(stream, JsonOptions, cancellationToken);
            return NormalizeManifest(manifest ?? new AdsManifest());
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Ads manifest {Bucket}/{Key} does not exist yet. Returning empty manifest.", Bucket, ManifestKey);
            return new AdsManifest { Version = 1, UpdatedAtUtc = DateTime.UtcNow, Items = new() };
        }
    }

    private AdminAdsManifestResponse ToAdminResponse(AdsManifest manifest)
    {
        return new AdminAdsManifestResponse
        {
            Version = manifest.Version,
            UpdatedAtUtc = manifest.UpdatedAtUtc,
            Items = manifest.Items
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .Select(x => new AdminAdBannerItem
                {
                    Id = x.Id,
                    Title = x.Title,
                    ImageKey = x.ImageKey,
                    ImageUrl = string.IsNullOrWhiteSpace(x.ImageKey) ? string.Empty : CreateSignedUrl(x.ImageKey),
                    TargetUrl = x.TargetUrl,
                    Enabled = x.Enabled,
                    Priority = x.Priority,
                    StartAtUtc = x.StartAtUtc,
                    EndAtUtc = x.EndAtUtc
                })
                .ToList()
        };
    }

    private AdsManifest NormalizeAndValidate(SaveAdsManifestRequest request)
    {
        var manifest = new AdsManifest
        {
            Version = Math.Max(1, request.Version),
            UpdatedAtUtc = DateTime.UtcNow,
            Items = new List<AdsManifestItem>()
        };

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in request.Items ?? new List<AdsManifestItem>())
        {
            var id = Slugify(raw.Id);
            if (string.IsNullOrWhiteSpace(id))
                id = Guid.NewGuid().ToString("N");
            if (!seenIds.Add(id))
                throw new ArgumentException($"Дубликат рекламного id: {id}");

            var title = (raw.Title ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title))
                title = id;

            var targetUrl = (raw.TargetUrl ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(targetUrl) &&
                (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                throw new ArgumentException($"Некорректная ссылка у баннера {id}.");
            }

            var imageKey = NormalizeImageKey(raw.ImageKey);
            if (!string.IsNullOrWhiteSpace(raw.ImageKey) && string.IsNullOrWhiteSpace(imageKey))
                throw new ArgumentException($"Некорректный ключ изображения у баннера {id}.");

            manifest.Items.Add(new AdsManifestItem
            {
                Id = id,
                Title = title,
                ImageKey = imageKey,
                TargetUrl = targetUrl,
                Enabled = raw.Enabled,
                Priority = raw.Priority,
                StartAtUtc = raw.StartAtUtc,
                EndAtUtc = raw.EndAtUtc
            });
        }

        return NormalizeManifest(manifest);
    }

    private AdsManifest NormalizeManifest(AdsManifest manifest)
    {
        manifest.Version = Math.Max(1, manifest.Version);
        if (manifest.UpdatedAtUtc == default)
            manifest.UpdatedAtUtc = DateTime.UtcNow;
        manifest.Items ??= new List<AdsManifestItem>();

        foreach (var item in manifest.Items)
        {
            item.Id = Slugify(item.Id);
            item.Title = (item.Title ?? string.Empty).Trim();
            item.ImageKey = NormalizeImageKey(item.ImageKey);
            item.TargetUrl = (item.TargetUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(item.Title))
                item.Title = item.Id;
        }

        return manifest;
    }

    private string CreateSignedUrl(string key)
    {
        key = NormalizeImageKey(key);
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Expires = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.SignedUrlTtlMinutes)),
            Verb = HttpVerb.GET,
            Protocol = Protocol.HTTPS
        };

        return _s3.GetPreSignedURL(request);
    }

    private string NormalizeImageKey(string? key)
    {
        key = (key ?? string.Empty).Replace("\\", "/").Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;
        if (key.Contains("..", StringComparison.Ordinal))
            return string.Empty;
        if (!key.StartsWith(ImagesPrefix, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return key;
    }

    private static string NormalizePrefix(string? value)
    {
        value = (value ?? "ads").Replace("\\", "/").Trim().Trim('/');
        return string.IsNullOrWhiteSpace(value) ? "ads" : value;
    }

    private static string FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string Slugify(string? value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = Regex.Replace(value, @"[^a-z0-9а-яё._-]+", "-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"-+", "-").Trim('-', '.', '_');
        return value.Length > 80 ? value[..80] : value;
    }
}
