using JaeZoo.Server.Models.Ads;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services.Admin;
using JaeZoo.Server.Services.Ads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Authorize(Policy = AuthPolicies.ManageAds)]
[Route("api/admin/ads")]
public sealed class AdminAdsController(IAdsService ads, AdminAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var manifest = await ads.GetAdminManifestAsync(cancellationToken);
        await audit.WriteAsync(User, HttpContext, "AdManifestViewed", "Ads", "manifest", $"Viewed ads manifest with {manifest.Items.Count} items.", cancellationToken);
        return Ok(manifest);
    }

    [HttpPut("manifest")]
    public async Task<IActionResult> Save([FromBody] SaveAdsManifestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await ads.SaveManifestAsync(request, cancellationToken);
            await audit.WriteAsync(User, HttpContext, "AdManifestPublished", "Ads", "manifest", $"Published ads manifest version {manifest.Version} with {manifest.Items.Count} items.", cancellationToken);
            return Ok(manifest);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("images")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage(IFormFile file, CancellationToken cancellationToken)
    {
        try
        {
            var response = await ads.UploadImageAsync(file, cancellationToken);
            await audit.WriteAsync(User, HttpContext, "AdImageUploaded", "AdsImage", response.ImageKey, $"Uploaded ad image {response.ImageKey}.", cancellationToken);
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
        await audit.WriteAsync(User, HttpContext, "AdImageDeleted", "AdsImage", imageKey, $"Deleted ad image {imageKey}.", cancellationToken);
        return NoContent();
    }
}
