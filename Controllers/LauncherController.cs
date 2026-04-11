using JaeZoo.Server.Models.Launcher;
using JaeZoo.Server.Services.Launcher;
using Microsoft.AspNetCore.Mvc;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/launcher")]
public sealed class LauncherController : ControllerBase
{
    private readonly ILauncherUpdateService _launcherUpdateService;

    public LauncherController(ILauncherUpdateService launcherUpdateService)
    {
        _launcherUpdateService = launcherUpdateService;
    }

    // Backward-compatible alias: old clients keep calling this and receive the client manifest.
    [HttpGet("manifest")]
    public Task<IActionResult> GetManifest([FromQuery] string? channel, CancellationToken cancellationToken)
        => GetClientManifest(channel, cancellationToken);

    [HttpGet("manifest/client")]
    public async Task<IActionResult> GetClientManifest([FromQuery] string? channel, CancellationToken cancellationToken)
    {
        var manifest = await _launcherUpdateService.GetClientManifestAsync(channel, cancellationToken);
        return Ok(await BuildManifestResponseAsync(manifest, isLauncher: false, cancellationToken));
    }

    [HttpGet("manifest/self")]
    public async Task<IActionResult> GetSelfManifest([FromQuery] string? channel, CancellationToken cancellationToken)
    {
        var manifest = await _launcherUpdateService.GetLauncherManifestAsync(channel, cancellationToken);
        return Ok(await BuildManifestResponseAsync(manifest, isLauncher: true, cancellationToken));
    }

    private async Task<object> BuildManifestResponseAsync(
        LauncherManifest manifest,
        bool isLauncher,
        CancellationToken cancellationToken)
    {
        var files = new List<object>(manifest.Files.Count);
        foreach (var file in manifest.Files)
        {
            var url = isLauncher
                ? await _launcherUpdateService.GetSignedLauncherFileUrlAsync(file.Path, manifest.Channel, cancellationToken)
                : await _launcherUpdateService.GetSignedClientFileUrlAsync(file.Path, manifest.Channel, cancellationToken);

            files.Add(new
            {
                path = file.Path,
                size = file.Size,
                sha256 = file.Sha256,
                url
            });
        }

        return new
        {
            channel = manifest.Channel,
            version = manifest.Version,
            entryExe = manifest.EntryExe,
            minLauncherVersion = manifest.MinLauncherVersion,
            files
        };
    }
}
