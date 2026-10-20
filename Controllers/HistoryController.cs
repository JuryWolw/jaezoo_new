using System.Security.Claims;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Services.History;
using JaeZoo.Server.Services.Mls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;

namespace JaeZoo.Server.Controllers;

/// <summary>
/// Multi-device history: the account's sealed history vault and the sealed message archive. The server stores
/// opaque blobs only (see Services/History/HistoryService.cs).
/// </summary>
[ApiController]
[Authorize]
[Route("api/history")]
public sealed class HistoryController(HistoryService history, IHubContext<ChatHub> hub) : ControllerBase
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

    /// <summary>The sealed vault (keyring + optional cloud-password wrap). Only for an active device of the account.</summary>
    [HttpGet("vault")]
    public Task<ActionResult> GetVault([FromQuery] Guid deviceId, CancellationToken ct) => Run(async () =>
    {
        var v = await history.GetVaultAsync(MeId, deviceId, ct);
        return v is null ? NotFound(new { code = "no_vault", error = "Хранилище истории ещё не создано." }) : Ok(v);
    });

    [HttpPost("vault")]
    public Task<ActionResult> CreateVault([FromBody] CreateVaultRequest body, CancellationToken ct) =>
        Run(async () => Ok(new { version = await history.CreateVaultAsync(MeId, body, ct) }));

    [HttpPut("vault")]
    public Task<ActionResult> UpdateVault([FromBody] UpdateVaultRequest body, CancellationToken ct) =>
        Run(async () => Ok(new { version = await history.UpdateVaultAsync(MeId, body, ct) }));

    [HttpPost("records")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public Task<ActionResult> Upload([FromBody] UploadHistoryRequest body, CancellationToken ct) =>
        Run(async () => Ok(await history.UploadAsync(MeId, body, ct)));

    [HttpGet("records")]
    public Task<ActionResult> Records([FromQuery] Guid deviceId, [FromQuery] string? after, [FromQuery] int take = 500, CancellationToken ct = default) =>
        Run(async () => Ok(await history.GetPageAsync(MeId, deviceId, after, take, ct)));

    // ---------- device link ----------

    /// <summary>A new device asks for the history key; the account's other devices get «HistoryAccessRequested».</summary>
    [HttpPost("requests")]
    [EnableRateLimiting("security-sensitive")]
    public Task<ActionResult> CreateRequest([FromBody] CreateAccessRequestRequest body, CancellationToken ct) => Run(async () =>
    {
        var me = MeId;
        var r = await history.CreateAccessRequestAsync(me, body, ct);
        await hub.Clients.User(me.ToString()).SendAsync("HistoryAccessRequested", new { id = r.Id, deviceId = r.DeviceId }, ct);
        return Ok(new { id = r.Id, expiresAt = r.ExpiresAt });
    });

    [HttpGet("requests")]
    public Task<ActionResult> PendingRequests([FromQuery] Guid deviceId, CancellationToken ct) =>
        Run(async () => Ok(await history.PendingAccessRequestsAsync(MeId, deviceId, ct)));

    [HttpGet("requests/{requestId:guid}")]
    public Task<ActionResult> RequestStatus(Guid requestId, [FromQuery] Guid deviceId, CancellationToken ct) =>
        Run(async () => Ok(new { status = await history.AccessRequestStatusAsync(MeId, deviceId, requestId, ct) }));

    [HttpPost("requests/{requestId:guid}/approve")]
    [EnableRateLimiting("security-sensitive")]
    public Task<ActionResult> Approve(Guid requestId, [FromBody] ApproveAccessRequestRequest body, CancellationToken ct) => Run(async () =>
    {
        var me = MeId;
        var r = await history.ApproveAccessRequestAsync(me, requestId, body, ct);
        await hub.Clients.User(me.ToString()).SendAsync("HistoryAccessResolved", new { id = r.Id, deviceId = r.DeviceId, status = (int)r.Status }, ct);
        await hub.Clients.User(me.ToString()).SendAsync("HistoryGrant", new { targetDeviceId = r.DeviceId }, ct);
        return NoContent();
    });

    [HttpPost("requests/{requestId:guid}/decline")]
    public Task<ActionResult> Decline(Guid requestId, [FromBody] DeclineAccessRequestRequest body, CancellationToken ct) => Run(async () =>
    {
        var me = MeId;
        var r = await history.DeclineAccessRequestAsync(me, requestId, body.DeviceId, ct);
        await hub.Clients.User(me.ToString()).SendAsync("HistoryAccessResolved", new { id = r.Id, deviceId = r.DeviceId, status = (int)r.Status }, ct);
        return NoContent();
    });

    [HttpPost("grants")]
    public Task<ActionResult> SendGrant([FromBody] SendGrantRequest body, CancellationToken ct) => Run(async () =>
    {
        var me = MeId;
        await history.SendGrantAsync(me, body, ct);
        await hub.Clients.User(me.ToString()).SendAsync("HistoryGrant", new { targetDeviceId = body.TargetDeviceId }, ct);
        return NoContent();
    });

    [HttpGet("grants")]
    public Task<ActionResult> Grants([FromQuery] Guid deviceId, CancellationToken ct) =>
        Run(async () => Ok(await history.GrantsAsync(MeId, deviceId, ct)));

    [HttpPost("grants/{grantId:guid}/ack")]
    public Task<ActionResult> AckGrant(Guid grantId, [FromQuery] Guid deviceId, CancellationToken ct) => Run(async () =>
    {
        await history.AckGrantAsync(MeId, deviceId, grantId, ct);
        return NoContent();
    });
}
