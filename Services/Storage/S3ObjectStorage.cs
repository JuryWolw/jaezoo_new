using Amazon.S3;
using Amazon.S3.Model;

namespace JaeZoo.Server.Services.Storage;

public sealed class S3ObjectStorage : IObjectStorage
{
    private readonly IAmazonS3 _s3;
    private readonly string _defaultBucket;
    private readonly string _publicBaseUrl;

    public S3ObjectStorage(IAmazonS3 s3, IConfiguration cfg)
    {
        _s3 = s3;
        _defaultBucket = cfg["ObjectStorage:Bucket"] ?? cfg["ObjectStorage:Buckets:Files"] ?? "jaezoo-files";
        _publicBaseUrl = (cfg["ObjectStorage:PublicBaseUrl"] ?? "https://storage.yandexcloud.net").TrimEnd('/');
    }

    public Task PutAsync(string key, Stream data, string contentType, CancellationToken ct)
        => PutAsync(_defaultBucket, key, data, contentType, ct);

    public async Task PutAsync(string bucket, string key, Stream data, string contentType, CancellationToken ct)
    {
        var req = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key.TrimStart('/'),
            InputStream = data,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            AutoCloseStream = false
        };

        await _s3.PutObjectAsync(req, ct);
    }

    public Task<(Stream Stream, string ContentType, long Size)> GetAsync(string key, CancellationToken ct)
        => GetAsync(_defaultBucket, key, ct);

    public async Task<(Stream Stream, string ContentType, long Size)> GetAsync(string bucket, string key, CancellationToken ct)
    {
        var res = await _s3.GetObjectAsync(bucket, key.TrimStart('/'), ct);
        return (
            res.ResponseStream,
            res.Headers.ContentType ?? "application/octet-stream",
            res.ContentLength
        );
    }

    public Task DeleteAsync(string key, CancellationToken ct)
        => DeleteAsync(_defaultBucket, key, ct);

    public Task DeleteAsync(string bucket, string key, CancellationToken ct)
        => _s3.DeleteObjectAsync(bucket, key.TrimStart('/'), ct);

    public string GetPublicUrl(string key) => GetPublicUrl(_defaultBucket, key);

    public string GetPublicUrl(string bucket, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Storage key is empty.", nameof(key));

        key = key.TrimStart('/');

        if (_publicBaseUrl.Contains("storage.yandexcloud.net", StringComparison.OrdinalIgnoreCase) ||
            _publicBaseUrl.Contains("s3.yandexcloud.net", StringComparison.OrdinalIgnoreCase))
        {
            return $"{_publicBaseUrl}/{bucket}/{key}";
        }

        return $"{_publicBaseUrl}/{bucket}/{key}";
    }
}
