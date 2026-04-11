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

    public async Task<LauncherManifest> GetManifestAsync(string? channel, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = NormalizeChannel(channel);
        var manifestKey = BuildManifestKey(normalizedChannel);

        _logger.LogInformation("Loading launcher manifest from bucket {Bucket}, key {Key}", _options.Bucket, manifestKey);

        using var response = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _options.Bucket,
            Key = manifestKey
        }, cancellationToken);

        await using var stream = response.ResponseStream;
        var manifest = await JsonSerializer.DeserializeAsync<LauncherManifest>(stream, JsonOptions, cancellationToken);

        if (manifest is null)
            throw new InvalidOperationException("Launcher manifest is empty or invalid.");

        manifest.Channel = normalizedChannel;
        return manifest;
    }

    public Task<string> GetSignedFileUrlAsync(string filePath, string? channel, CancellationToken cancellationToken = default)
    {
        var normalizedChannel = NormalizeChannel(channel);
        var normalizedPath = NormalizeFilePath(filePath);

        if (string.IsNullOrWhiteSpace(normalizedPath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var key = $"{normalizedChannel}/files/{normalizedPath}".Replace("\\", "/");

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

    private string BuildManifestKey(string channel)
    {
        if (!string.IsNullOrWhiteSpace(_options.ManifestKey) &&
            _options.ManifestKey.StartsWith($"{channel}/", StringComparison.OrdinalIgnoreCase))
        {
            return _options.ManifestKey.Replace("\\", "/");
        }

        return $"{channel}/manifest.json";
    }

    private static string NormalizeFilePath(string filePath)
    {
        return filePath.Replace("\\", "/").Trim().TrimStart('/');
    }
}
