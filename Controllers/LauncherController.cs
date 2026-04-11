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

    [HttpGet("manifest")]
    public async Task<IActionResult> GetManifest([FromQuery] string? channel, CancellationToken cancellationToken)
    {
        var manifest = await _launcherUpdateService.GetManifestAsync(channel, cancellationToken);

        var files = new List<object>(manifest.Files.Count);
        foreach (var file in manifest.Files)
        {
            var url = await _launcherUpdateService.GetSignedFileUrlAsync(file.Path, manifest.Channel, cancellationToken);
            files.Add(new
            {
                path = file.Path,
                size = file.Size,
                sha256 = file.Sha256,
                url
            });
        }

        return Ok(new
        {
            channel = manifest.Channel,
            version = manifest.Version,
            entryExe = manifest.EntryExe,
            minLauncherVersion = manifest.MinLauncherVersion,
            files
        });
    }
}
