using JaeZoo.Server.Models.Launcher;

namespace JaeZoo.Server.Services.Launcher;

public interface ILauncherUpdateService
{
    Task<LauncherManifest> GetManifestAsync(string? channel, CancellationToken cancellationToken = default);
    Task<string> GetSignedFileUrlAsync(string filePath, string? channel, CancellationToken cancellationToken = default);
}
