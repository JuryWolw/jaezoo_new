using JaeZoo.Server.Models.Files;

namespace JaeZoo.Server.Services.Files;

public sealed class FileBucketRouter(IConfiguration cfg)
{
    public string GetBucket(StoredFileKind kind)
    {
        var section = cfg.GetSection("ObjectStorage:Buckets");
        var fallback = cfg["ObjectStorage:Bucket"] ?? "jaezoo-files";

        return kind switch
        {
            StoredFileKind.Avatar => section["Avatars"] ?? "jaezoo-avatars",
            StoredFileKind.Photo => section["Photos"] ?? "jaezoo-photos",
            StoredFileKind.Video => section["Videos"] ?? "jaezoo-videos",
            StoredFileKind.Music => section["Music"] ?? "jaezoo-music",
            _ => section["Files"] ?? fallback
        };
    }

    public static string BuildObjectKey(StoredFileKind kind, DateTime nowUtc, Guid objectId, string extension)
    {
        var prefix = kind switch
        {
            StoredFileKind.Avatar => "avatars",
            StoredFileKind.Photo => "photos",
            StoredFileKind.Video => "videos",
            StoredFileKind.Music => "music",
            _ => "files"
        };

        extension = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension.ToLowerInvariant();
        return $"{prefix}/{nowUtc:yyyy}/{nowUtc:MM}/{objectId:N}{extension}";
    }
}
