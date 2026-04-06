namespace JaeZoo.Server.Services.Storage;

public interface IObjectStorage
{
    Task PutAsync(string key, Stream data, string contentType, CancellationToken ct);
    Task<(Stream Stream, string ContentType, long Size)> GetAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);

    string GetPublicUrl(string key);
}