using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Files;

public sealed class FileCleanupService(
    AppDbContext db,
    IObjectStorage storage,
    ILogger<FileCleanupService> log)
{
    public async Task DeleteFilesForMessageAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0) return;

        var files = await db.ChatFiles
            .Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var file in files)
        {
            file.DeletedAt = now;
            await DeleteObjectIfNoActiveReferencesAsync(file, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteObjectIfNoActiveReferencesAsync(ChatFile file, CancellationToken ct)
    {
        var bucket = string.IsNullOrWhiteSpace(file.Bucket) ? "jaezoo-files" : file.Bucket;
        var key = string.IsNullOrWhiteSpace(file.ObjectKey) ? file.StoredPath : file.ObjectKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        var stillUsed = await db.ChatFiles.AsNoTracking().AnyAsync(f =>
            f.Id != file.Id &&
            f.DeletedAt == null &&
            f.BlockedAt == null &&
            f.Bucket == bucket &&
            (f.ObjectKey == key || f.StoredPath == key), ct);

        if (stillUsed)
        {
            log.LogInformation("Storage object kept because another active ChatFile references it. Bucket={Bucket} Key={Key} FileId={FileId}", bucket, key, file.Id);
            return;
        }

        try
        {
            await storage.DeleteAsync(bucket, key, ct);
            log.LogInformation("Storage object deleted. Bucket={Bucket} Key={Key} FileId={FileId}", bucket, key, file.Id);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to delete storage object. Bucket={Bucket} Key={Key} FileId={FileId}", bucket, key, file.Id);
        }
    }
}
