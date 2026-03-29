namespace JaeZoo.Server.Services.Storage;

public sealed class LocalObjectStorage : IObjectStorage
{
    private readonly string _root;
    private readonly string _publicBaseUrl;

    public LocalObjectStorage(IWebHostEnvironment env, IConfiguration cfg)
    {
        var storagePath = (cfg.GetValue<string>("Files:StoragePath") ?? "data/uploads").Trim();
        _root = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.Combine(env.ContentRootPath, storagePath);

        Directory.CreateDirectory(_root);
        _publicBaseUrl = (cfg["Files:PublicBaseUrl"] ?? "/api/files").TrimEnd('/');
    }

    public async Task PutAsync(string key, Stream data, string contentType, CancellationToken ct)
    {
        var path = GetAbsolutePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = File.Create(path);
        await data.CopyToAsync(output, ct);
    }

    public Task<(Stream Stream, string ContentType, long Size)> GetAsync(string key, CancellationToken ct)
    {
        var path = GetAbsolutePath(key);
        if (!File.Exists(path))
            throw new FileNotFoundException("Object was not found.", path);

        Stream stream = File.OpenRead(path);
        var info = new FileInfo(path);
        return Task.FromResult((stream, "application/octet-stream", info.Length));
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = GetAbsolutePath(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public string GetPublicUrl(string key)
    {
        key = key.TrimStart('/');
        return $"{_publicBaseUrl}/{key}";
    }

    private string GetAbsolutePath(string key)
        => Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
}
