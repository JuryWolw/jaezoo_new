using System.Security.Claims;
using JaeZoo.Server.Services.History;
using JaeZoo.Server.Services.Mls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JaeZoo.Server.Controllers;

/// <summary>
/// Multi-device history: the account's sealed history vault and the sealed message archive. The server stores
/// opaque blobs only (see Services/History/HistoryService.cs).
/// </summary>
[ApiController]
[Authorize]
[Route("api/history")]
public sealed class HistoryController(HistoryService history) : ControllerBase
{
    private Guid MeId
    {
        get
        {
            var s = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(s, out var id) ? id : throw new UnauthorizedAccessException("No user id claim.");
        }
    }

    private async Task<ActionResult> Run(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (HistoryException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, error = ex.Message });
        }
        catch (MlsDeliveryException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, error = ex.Message });
        }
    }

    /// <summary>The sealed vault (keyring + optional cloud-password wrap). Rate-limited: it is what a password guess needs.</summary>
    [HttpGet("vault")]
    [EnableRateLimiting("security-sensitive")]
    public Task<ActionResult> GetVault([FromQuery] Guid deviceId, CancellationToken ct) => Run(async () =>
    {
        var v = await history.GetVaultAsync(MeId, deviceId, ct);
        return v is null ? NotFound(new { code = "no_vault", error = "Хранилище истории ещё не создано." }) : Ok(v);
    });

    [HttpPost("vault")]
    public Task<ActionResult> CreateVault([FromBody] CreateVaultRequest body, CancellationToken ct) =>
        Run(async () => Ok(new { version = await history.CreateVaultAsync(MeId, body, ct) }));

    [HttpPut("vault")]
    [EnableRateLimiting("security-sensitive")]
    public Task<ActionResult> UpdateVault([FromBody] UpdateVaultRequest body, CancellationToken ct) =>
        Run(async () => Ok(new { version = await history.UpdateVaultAsync(MeId, body, ct) }));

    [HttpPost("records")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public Task<ActionResult> Upload([FromBody] UploadHistoryRequest body, CancellationToken ct) =>
        Run(async () => Ok(await history.UploadAsync(MeId, body, ct)));

    [HttpGet("records")]
    public Task<ActionResult> Records([FromQuery] Guid deviceId, [FromQuery] string? after, [FromQuery] int take = 500, CancellationToken ct = default) =>
        Run(async () => Ok(await history.GetPageAsync(MeId, deviceId, after, take, ct)));
}
