namespace JaeZoo.Server.Services.Storage;

public sealed class LocalObjectStorage : IObjectStorage
{
    private readonly string _root;
    private readonly string _publicBaseUrl;
    private readonly string _defaultBucket;

    public LocalObjectStorage(IWebHostEnvironment env, IConfiguration cfg)
    {
        var storagePath = (cfg.GetValue<string>("Files:StoragePath") ?? "data/uploads").Trim();
        _root = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.Combine(env.ContentRootPath, storagePath);

        Directory.CreateDirectory(_root);
        _publicBaseUrl = (cfg["Files:PublicBaseUrl"] ?? "/api/files").TrimEnd('/');
        _defaultBucket = cfg["ObjectStorage:Buckets:Files"] ?? cfg["ObjectStorage:Bucket"] ?? "jaezoo-files";
    }

    public Task PutAsync(string key, Stream data, string contentType, CancellationToken ct)
        => PutAsync(_defaultBucket, key, data, contentType, ct);

    public async Task PutAsync(string bucket, string key, Stream data, string contentType, CancellationToken ct)
    {
        var path = GetAbsolutePath(bucket, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = File.Create(path);
        await data.CopyToAsync(output, ct);
    }

    public Task<(Stream Stream, string ContentType, long Size)> GetAsync(string key, CancellationToken ct)
        => GetAsync(_defaultBucket, key, ct);

    public Task<(Stream Stream, string ContentType, long Size)> GetAsync(string bucket, string key, CancellationToken ct)
    {
        var path = GetAbsolutePath(bucket, key);
        if (!File.Exists(path))
            throw new FileNotFoundException("Object was not found.", path);

        Stream stream = File.OpenRead(path);
        var info = new FileInfo(path);
        return Task.FromResult((stream, "application/octet-stream", info.Length));
    }

    public Task DeleteAsync(string key, CancellationToken ct)
        => DeleteAsync(_defaultBucket, key, ct);

    public Task DeleteAsync(string bucket, string key, CancellationToken ct)
    {
        var path = GetAbsolutePath(bucket, key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public string GetPublicUrl(string key) => GetPublicUrl(_defaultBucket, key);

    public string GetPublicUrl(string bucket, string key)
    {
        key = key.TrimStart('/');
        return $"{_publicBaseUrl}/{bucket}/{key}";
    }

    private string GetAbsolutePath(string bucket, string key)
        => Path.Combine(_root, bucket, key.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
}
