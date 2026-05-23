using JaeZoo.Server.Data;
using JaeZoo.Server.Models.Files;
using JaeZoo.Server.Options;
using JaeZoo.Server.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Files;

public sealed class FileScanHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<FileAntivirusOptions> options,
    ILogger<FileScanHostedService> log) : BackgroundService
{
    private readonly FileAntivirusOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            log.LogInformation("File antivirus scanner is disabled.");
            return;
        }

        log.LogInformation("File antivirus scanner started. Mode={Mode} PollSeconds={PollSeconds} BatchSize={BatchSize}",
            _options.Mode, _options.PollSeconds, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "File antivirus scanner batch failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), stoppingToken);
        }
    }

    private async Task ScanBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var scanner = scope.ServiceProvider.GetRequiredService<IFileAntivirusScanner>();
        var moderation = scope.ServiceProvider.GetRequiredService<FileModerationService>();

        var batchSize = Math.Clamp(_options.BatchSize, 1, 16);
        var files = await db.ChatFiles
            .Where(f => f.DeletedAt == null && f.BlockedAt == null)
            .Where(f => f.ScanStatus == FileScanStatus.Pending || f.ScanStatus == FileScanStatus.NotScanned || f.ScanStatus == FileScanStatus.MetadataChecked)
            .OrderBy(f => f.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var file in files)
        {
            var bucket = string.IsNullOrWhiteSpace(file.Bucket) ? "jaezoo-files" : file.Bucket;
            var key = string.IsNullOrWhiteSpace(file.ObjectKey) ? file.StoredPath : file.ObjectKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                file.ScanStatus = FileScanStatus.Failed;
                file.RiskNote = "Storage object key is empty.";
                await db.SaveChangesAsync(ct);
                continue;
            }

            file.ScanStatus = FileScanStatus.Pending;
            await db.SaveChangesAsync(ct);

            try
            {
                await using var stream = (await storage.GetAsync(bucket, key, ct)).Stream;
                var result = await scanner.ScanAsync(file, stream, ct);

                if (result.IsDangerous)
                {
                    var sha = (file.Sha256 ?? string.Empty).Trim().ToUpperInvariant();
                    var isAllowListed = !string.IsNullOrWhiteSpace(sha) &&
                        await db.FileScanAllowList.AsNoTracking().AnyAsync(a => a.Sha256 == sha, ct);

                    if (isAllowListed)
                    {
                        log.LogInformation("File scan threat ignored by allowlist. FileId={FileId} Sha256={Sha256} Reason={Reason}", file.Id, sha, result.Reason);
                        await moderation.MarkCleanAndBroadcastAsync(file.Id, ct);
                        continue;
                    }

                    await moderation.RemoveDangerousFileAsync(file.Id, result.Reason ?? "Blocked by antivirus scanner.", ct);
                    continue;
                }

                if (result.IsClean)
                {
                    await moderation.MarkCleanAndBroadcastAsync(file.Id, ct);
                    continue;
                }

                var fresh = await db.ChatFiles.FirstOrDefaultAsync(f => f.Id == file.Id, ct);
                if (fresh is not null && fresh.DeletedAt == null && fresh.BlockedAt == null)
                {
                    fresh.ScanStatus = FileScanStatus.Failed;
                    fresh.RiskNote = result.Reason ?? "Antivirus scan failed.";
                    await db.SaveChangesAsync(ct);
                    await moderation.BroadcastFileMessagesUpdatedAsync(fresh.Id, ct);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "File scan failed. FileId={FileId} Bucket={Bucket} Key={Key}", file.Id, bucket, key);
                var fresh = await db.ChatFiles.FirstOrDefaultAsync(f => f.Id == file.Id, ct);
                if (fresh is not null && fresh.DeletedAt == null && fresh.BlockedAt == null)
                {
                    fresh.ScanStatus = FileScanStatus.Failed;
                    fresh.RiskNote = ex.Message;
                    await db.SaveChangesAsync(ct);
                    await moderation.BroadcastFileMessagesUpdatedAsync(fresh.Id, ct);
                }
            }
        }
    }
}
