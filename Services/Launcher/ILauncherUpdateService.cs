using JaeZoo.Server.Models.Launcher;

namespace JaeZoo.Server.Services.Launcher;

public interface ILauncherUpdateService
{
    Task<LauncherManifest> GetClientManifestAsync(string? channel, CancellationToken cancellationToken = default);
    Task<LauncherManifest> GetLauncherManifestAsync(string? channel, CancellationToken cancellationToken = default);
    Task<string> GetSignedClientFileUrlAsync(string filePath, string? channel, CancellationToken cancellationToken = default);
    Task<string> GetSignedLauncherFileUrlAsync(string filePath, string? channel, CancellationToken cancellationToken = default);
    Task<string> GetSignedClientPackageUrlAsync(string? channel, CancellationToken cancellationToken = default);
    Task<string> GetSignedLauncherPackageUrlAsync(string? channel, CancellationToken cancellationToken = default);

    // Backward-compatible aliases for the original single-manifest API.
    Task<LauncherManifest> GetManifestAsync(string? channel, CancellationToken cancellationToken = default);
    Task<string> GetSignedFileUrlAsync(string filePath, string? channel, CancellationToken cancellationToken = default);
}
