using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using JaeZoo.Server.Models.Launcher;
using JaeZoo.Server.Options;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Launcher;

public sealed class LauncherUpdateService : ILauncherUpdateService
{
    private readonly LauncherUpdatesOptions _options;
    private readonly IAmazonS3 _s3;
    private readonly ILogger<LauncherUpdateService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public LauncherUpdateService(
        IOptions<LauncherUpdatesOptions> options,
        ILogger<LauncherUpdateService> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.Bucket))
            throw new InvalidOperationException("LauncherUpdates:Bucket is missing.");

        if (string.IsNullOrWhiteSpace(_options.AccessKey))
            throw new InvalidOperationException("LauncherUpdates:AccessKey is missing.");

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new InvalidOperationException("LauncherUpdates:SecretKey is missing.");

        var creds = new BasicAWSCredentials(_options.AccessKey, _options.SecretKey);
        var cfg = new AmazonS3Config
        {
            ServiceURL = _options.Endpoint,
            AuthenticationRegion = _options.Region,
            ForcePathStyle = true
        };

        _s3 = new AmazonS3Client(creds, cfg);
    }

    public Task<LauncherManifest> GetManifestAsync(string? channel, CancellationToken cancellationToken = default)
        => GetClientManifestAsync(channel, cancellationToken);

    public Task<string> GetSignedFileUrlAsync(string filePath, string? channel, CancellationToken cancellationToken = default)
        => GetSignedClientFileUrlAsync(filePath, channel, cancellationToken);

    public Task<LauncherManifest> GetClientManifestAsync(string? channel, CancellationToken cancellationToken = default)
        => GetManifestCoreAsync(UpdateTarget.Client, channel, cancellationToken);

    public Task<LauncherManifest> GetLauncherManifestAsync(string? channel, CancellationToken cancellationToken = default)
        => GetManifestCoreAsync(UpdateTarget.Launcher, channel, cancellationToken);

    public Task<string> GetSignedClientFileUrlAsync(string filePath, string? channel, CancellationToken cancellationToken = default)
        => GetSignedFileUrlCoreAsync(UpdateTarget.Client, filePath, channel, cancellationToken);

    public Task<string> GetSignedLauncherFileUrlAsync(string filePath, string? channel, CancellationToken cancellationToken = default)
        => GetSignedFileUrlCoreAsync(UpdateTarget.Launcher, filePath, channel, cancellationToken);

    public Task<string> GetSignedClientPackageUrlAsync(string? channel, CancellationToken cancellationToken = default)
        => GetSignedPackageUrlCoreAsync(UpdateTarget.Client, channel, cancellationToken);

    public Task<string> GetSignedLauncherPackageUrlAsync(string? channel, CancellationToken cancellationToken = default)
        => GetSignedPackageUrlCoreAsync(UpdateTarget.Launcher, channel, cancellationToken);

    private Task<string> GetSignedPackageUrlCoreAsync(UpdateTarget target, string? channel, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (string.IsNullOrWhiteSpace(_options.CdnBaseUrl))
            throw new InvalidOperationException("LauncherUpdates:CdnBaseUrl is missing.");

        if (string.IsNullOrWhiteSpace(_options.CdnSecureKey))
            throw new InvalidOperationException("LauncherUpdates:CdnSecureKey is missing.");

        var normalizedChannel = NormalizeChannel(channel);
        var packagePath = "/" + BuildPackageKey(target, normalizedChannel).TrimStart('/');
        var baseUrl = _options.CdnBaseUrl.TrimEnd('/');
        var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(30, _options.PackageUrlTtlSeconds);

        // Yandex Cloud CDN secure token format without IP restriction:
        // md5 = base64url(md5("{expires}{path} {secret}"))
        var payload = $"{expires}{packagePath} {_options.CdnSecureKey}";
        using var md5 = MD5.Create();
        var digest = md5.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var token = Convert.ToBase64String(digest)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var signedUrl = $"{baseUrl}{packagePath}?md5={Uri.EscapeDataString(token)}&expires={expires}";
        return Task.FromResult(signedUrl);
    }

    private async Task<LauncherManifest> GetManifestCoreAsync(UpdateTarget target, string? channel, CancellationToken cancellationToken)
    {
        var normalizedChannel = NormalizeChannel(channel);
        var manifestKey = BuildManifestKey(target, normalizedChannel);

        _logger.LogInformation(
            "Loading update manifest from bucket {Bucket}, target {Target}, key {Key}",
            _options.Bucket,
            target,
            manifestKey);

        using var response = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _options.Bucket,
            Key = manifestKey
        }, cancellationToken);

        await using var stream = response.ResponseStream;
        var manifest = await JsonSerializer.DeserializeAsync<LauncherManifest>(stream, JsonOptions, cancellationToken);

        if (manifest is null)
            throw new InvalidOperationException($"{target} manifest is empty or invalid.");

        manifest.Channel = normalizedChannel;
        return manifest;
    }

    private Task<string> GetSignedFileUrlCoreAsync(UpdateTarget target, string filePath, string? channel, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var normalizedChannel = NormalizeChannel(channel);
        var normalizedPath = NormalizeFilePath(filePath);

        if (string.IsNullOrWhiteSpace(normalizedPath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var key = BuildFilesPrefix(target, normalizedChannel) + normalizedPath;

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Expires = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.SignedUrlTtlMinutes)),
            Verb = HttpVerb.GET,
            Protocol = Protocol.HTTPS
        };

        var url = _s3.GetPreSignedURL(request);
        return Task.FromResult(url);
    }

    private string NormalizeChannel(string? channel)
    {
        return string.IsNullOrWhiteSpace(channel)
            ? _options.Channel
            : channel.Trim().ToLowerInvariant();
    }

    private string BuildManifestKey(UpdateTarget target, string channel)
    {
        var explicitKey = target == UpdateTarget.Client
            ? _options.ClientManifestKey
            : _options.LauncherManifestKey;

        if (!string.IsNullOrWhiteSpace(explicitKey))
            return explicitKey.Replace("\\", "/");

        var area = target == UpdateTarget.Client ? "client" : "launcher";
        return $"{channel}/{area}/manifest.json";
    }

    private string BuildPackageKey(UpdateTarget target, string channel)
    {
        if (target == UpdateTarget.Launcher && !string.IsNullOrWhiteSpace(_options.PackageKey))
            return _options.PackageKey!.Replace("\\", "/");

        var area = target == UpdateTarget.Client ? "client" : "launcher";
        return $"{channel}/{area}/package.zip";
    }

    private static string BuildFilesPrefix(UpdateTarget target, string channel)
    {
        var area = target == UpdateTarget.Client ? "client" : "launcher";
        return $"{channel}/{area}/files/";
    }

    private static string NormalizeFilePath(string filePath)
    {
        return filePath.Replace("\\", "/").Trim().TrimStart('/');
    }

    private enum UpdateTarget
    {
        Client,
        Launcher
    }
}
