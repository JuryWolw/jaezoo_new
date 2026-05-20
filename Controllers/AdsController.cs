using JaeZoo.Server.Services.Ads;
using Microsoft.AspNetCore.Mvc;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/ads")]
public sealed class AdsController(IAdsService ads) : ControllerBase
{
    [HttpGet]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var response = await ads.GetPublicAdsAsync(cancellationToken);
        return Ok(response);
    }
}
