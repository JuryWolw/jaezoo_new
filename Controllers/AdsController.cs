using JaeZoo.Server.Services.Ads;
using Microsoft.AspNetCore.Mvc;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/ads")]
public sealed class AdsController(IAdsService ads) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        // Ads are managed live through AdsManager, so do not let browser/proxy caches
        // keep an old manifest after publishing a new banner.
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var response = await ads.GetPublicAdsAsync(cancellationToken);
        return Ok(response);
    }
}
