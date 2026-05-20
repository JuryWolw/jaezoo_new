using JaeZoo.Server.Models.Ads;
using JaeZoo.Server.Services.Ads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/admin/ads")]
public sealed class AdminAdsController(IAdsService ads) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var manifest = await ads.GetAdminManifestAsync(cancellationToken);
        return Ok(manifest);
    }

    [HttpPut("manifest")]
    public async Task<IActionResult> Save([FromBody] SaveAdsManifestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await ads.SaveManifestAsync(request, cancellationToken);
            return Ok(manifest);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("images")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        try
        {
            var response = await ads.UploadImageAsync(file, cancellationToken);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("images")]
    public async Task<IActionResult> DeleteImage([FromQuery] string imageKey, CancellationToken cancellationToken)
    {
        await ads.DeleteImageAsync(imageKey, cancellationToken);
        return NoContent();
    }
}
