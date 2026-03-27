using Amazon.S3;
using Amazon.S3.Model;

namespace JaeZoo.Server.Services.Storage;

public sealed class S3ObjectStorage : IObjectStorage
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _publicBaseUrl;

    public S3ObjectStorage(IAmazonS3 s3, IConfiguration cfg)
    {
        _s3 = s3;
        _bucket = cfg["ObjectStorage:Bucket"] ?? throw new InvalidOperationException("ObjectStorage:Bucket missing");
        _publicBaseUrl = (cfg["ObjectStorage:PublicBaseUrl"] ?? throw new InvalidOperationException("ObjectStorage:PublicBaseUrl missing"))
            .TrimEnd('/');
    }

    public async Task PutAsync(string key, Stream data, string contentType, CancellationToken ct)
    {
        var req = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = data,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            AutoCloseStream = false
        };

        await _s3.PutObjectAsync(req, ct);
    }

    public async Task<(Stream Stream, string ContentType, long Size)> GetAsync(string key, CancellationToken ct)
    {
        var res = await _s3.GetObjectAsync(_bucket, key, ct);
        return (
            res.ResponseStream,
            res.Headers.ContentType ?? "application/octet-stream",
            res.ContentLength
        );
    }

    public Task DeleteAsync(string key, CancellationToken ct)
        => _s3.DeleteObjectAsync(_bucket, key, ct);

    public string GetPublicUrl(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Storage key is empty.", nameof(key));

        key = key.TrimStart('/');
        return $"{_publicBaseUrl}/{key}";
    }
}