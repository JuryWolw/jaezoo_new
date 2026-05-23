using JaeZoo.Server.Models;

namespace JaeZoo.Server.Services.Files;

public interface IFileAntivirusScanner
{
    Task<FileScanResult> ScanAsync(ChatFile file, Stream content, CancellationToken ct);
}
