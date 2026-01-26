using Amazon.S3;
using Amazon.S3.Model;

namespace JaeZoo.Server.Services.Storage;

public sealed class B2S3Storage : IObjectStorage
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public B2S3Storage(IAmazonS3 s3, IConfiguration cfg)
    {
        _s3 = s3;
        _bucket = cfg["ObjectStorage:Bucket"] ?? throw new InvalidOperationException("ObjectStorage:Bucket missing");
    }

    public async Task PutAsync(string key, Stream data, string contentType, CancellationToken ct)
    {
        var req = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = data,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
        };

        await _s3.PutObjectAsync(req, ct);
    }

    public async Task<(Stream Stream, string ContentType, long Size)> GetAsync(string key, CancellationToken ct)
    {
        var res = await _s3.GetObjectAsync(_bucket, key, ct);
        return (res.ResponseStream,
                res.Headers.ContentType ?? "application/octet-stream",
                res.ContentLength);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
        => _s3.DeleteObjectAsync(_bucket, key, ct);
}
