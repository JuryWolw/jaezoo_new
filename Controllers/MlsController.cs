using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Mls;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services.Chat;
using JaeZoo.Server.Services.Mls;
using JaeZoo.Server.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

/// <summary>MLS Delivery Service API. Everything here is opaque bytes plus delivery metadata.</summary>
[ApiController]
[Route("api/mls")]
[Authorize]
public sealed class MlsController(
    AppDbContext db,
    MlsDeliveryService mls,
    DirectChatService directChats,
    GroupChatService groupChats,
    IHubContext<ChatHub> hub,
    SecurityAuditService securityAudit,
    ILogger<MlsController> log) : ControllerBase
{
    private Guid MeId
    {
        get
        {
            var s = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(s, out var id) ? id : throw new UnauthorizedAccessException("No user id claim.");
        }
    }

    private Guid? SessionId => Guid.TryParse(User.FindFirst("sid")?.Value, out var sid) ? sid : null;

    private async Task<ActionResult> Run(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (MlsDeliveryException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.Code, error = ex.Message });
        }
    }

    // ---------- devices ----------

    [HttpPost("devices")]
    [EnableRateLimiting("security-sensitive")]
    public Task<ActionResult> RegisterDevice([FromBody] RegisterDeviceRequest body, CancellationToken ct) => Run(async () =>
    {
        var me = MeId;
        var device = await mls.RegisterDeviceAsync(me, SessionId, body, ct);

        // Members of this user's groups (and the user's other devices) add the device lazily.
        var notify = await mls.UsersSharingGroupsWithUserAsync(me, ct);
        await hub.Clients.Users(notify.Append(me).Distinct().Select(u => u.ToString()).ToList())
            .SendAsync("MlsDeviceAdded", new DeviceRefDto(me, device.Id), ct);

        return Ok(new UserDeviceDto(device.Id, device.Name, device.Platform, device.ClientVersion, device.CreatedAt, device.LastSeenAt, true, null, null));
    });

    [HttpGet("devices/me")]
    public Task<ActionResult> MyDevices(CancellationToken ct) => Run(async () => Ok(await mls.ListMyDevicesAsync(MeId, SessionId, ct)));

    [HttpPatch("devices/{deviceId:guid}/name")]
    public Task<ActionResult> RenameDevice(Guid deviceId, [FromBody] RenameDeviceRequest body, CancellationToken ct) => Run(async () =>
    {
        await mls.RenameDeviceAsync(MeId, deviceId, body?.Name, ct);
        return NoContent();
    });

    /// <summary>Ends the device's session and asks the other members' clients to remove its leaves.</summary>
    [HttpPost("devices/{deviceId:guid}/revoke")]
    [EnableRateLimiting("security-sensitive")]
    public Task<ActionResult> RevokeDevice(Guid deviceId, CancellationToken ct) => Run(async () =>
    {
        var me = MeId;
        var notify = await mls.RevokeDeviceAsync(me, deviceId, "user", ct);
        await NotifyDeviceRevokedAsync(me, deviceId, notify, ct);
        await securityAudit.TryWriteAsync(User, HttpContext, "Security.DeviceRevoked", "Device", deviceId.ToString(), "Device revoked by user", ct);
        return NoContent();
    });

    private async Task NotifyDeviceRevokedAsync(Guid userId, Guid deviceId, IEnumerable<Guid> users, CancellationToken ct)
    {
        var dto = new MlsDeviceRevokedDto(userId, deviceId);
        var targets = users.Append(userId).Distinct().Select(u => u.ToString()).ToList();
        await hub.Clients.Users(targets).SendAsync("MlsDeviceRevoked", dto, ct);
    }

    // ---------- key packages ----------

    [HttpPost("key-packages")]
    public Task<ActionResult> UploadKeyPackages([FromBody] UploadKeyPackagesRequest body, CancellationToken ct) =>
        Run(async () => Ok(await mls.UploadKeyPackagesAsync(MeId, body, ct)));

    [HttpGet("key-packages/status")]
    public Task<ActionResult> KeyPackageStatus([FromQuery] Guid deviceId, CancellationToken ct) =>
        Run(async () => Ok(await mls.KeyPackageStatusAsync(MeId, deviceId, ct)));

    [HttpPost("key-packages/claim")]
    [EnableRateLimiting("chat-write")]
    public Task<ActionResult> ClaimKeyPackages([FromBody] ClaimKeyPackagesRequest body, CancellationToken ct) =>
        Run(async () => Ok(await mls.ClaimKeyPackagesAsync(MeId, body?.DeviceIds ?? Array.Empty<Guid>(), ct)));

    // ---------- groups ----------

    [HttpGet("conversations/{kind}/{conversationId:guid}")]
    public Task<ActionResult> Conversation(MlsConversationKind kind, Guid conversationId, CancellationToken ct) =>
        Run(async () => Ok(await mls.GetConversationAsync(MeId, kind, conversationId, ct)));

    [HttpPost("groups")]
    [RequireVerifiedEmail]
    public Task<ActionResult> CreateGroup([FromBody] CreateMlsGroupRequest body, CancellationToken ct) => Run(async () =>
    {
        var group = await mls.CreateGroupAsync(MeId, body, ct);
        return Ok(await mls.GetConversationAsync(MeId, group.Kind, body.ConversationId, ct));
    });

    [HttpPost("groups/{groupId:guid}/commits")]
    [EnableRateLimiting("chat-write")]
    public Task<ActionResult> SubmitCommit(Guid groupId, [FromBody] SubmitCommitRequest body, CancellationToken ct) => Run(async () =>
    {
        var me = MeId;
        var (ev, welcomes, participants) = await mls.SubmitCommitAsync(me, groupId, body, ct);

        var eventDto = new MlsEventDto(ev.Id, ev.GroupId, ev.Kind, ev.Epoch, ev.SenderUserId, ev.SenderDeviceId, ev.CreatedAt, ev.Payload, null);
        await hub.Clients.Users(participants.Select(p => p.ToString()).ToList()).SendAsync("MlsCommit", eventDto, ct);

        if (welcomes.Count > 0)
        {
            var group = await db.MlsGroups.AsNoTracking().FirstAsync(g => g.Id == groupId, ct);
            foreach (var w in welcomes)
            {
                var conversationId = await mls.ConversationIdForViewerAsync(group, w.TargetUserId, ct);
                var dto = new MlsWelcomeDto(w.Id, w.GroupId, w.Epoch, w.CommitEventId, group.Kind, conversationId, w.Data);
                await hub.Clients.User(w.TargetUserId.ToString()).SendAsync("MlsWelcome", dto, ct);
            }
        }

        log.LogInformation("MLS commit accepted. Group={GroupId} Epoch={Epoch} Event={EventId}", groupId, ev.Epoch + 1, ev.Id);
        return Ok(new CommitAcceptedDto(ev.Id, ev.Epoch + 1));
    });

    /// <summary>Ordered events after <paramref name="after"/>: commits and message events with current message data.</summary>
    [HttpGet("groups/{groupId:guid}/events")]
    public Task<ActionResult> Events(Guid groupId, [FromQuery] long after = 0, [FromQuery] int take = 200, CancellationToken ct = default) => Run(async () =>
    {
        var me = MeId;
        var events = await mls.GetEventsAsync(me, groupId, after, take, ct);
        var messageIds = events.Where(e => e.MessageId.HasValue).Select(e => e.MessageId!.Value).Distinct().ToList();
        var messages = await LoadMessagesAsync(groupId, messageIds, ct);

        return Ok(events.Select(e => new MlsEventDto(
            e.Id, e.GroupId, e.Kind, e.Epoch, e.SenderUserId, e.SenderDeviceId, e.CreatedAt,
            e.Kind == MlsEventKind.Commit ? e.Payload : null,
            e.MessageId.HasValue && messages.TryGetValue(e.MessageId.Value, out var m) ? m : null)).ToList());
    });

    private async Task<Dictionary<Guid, MessageDto>> LoadMessagesAsync(Guid groupId, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        var group = await db.MlsGroups.AsNoTracking().FirstAsync(g => g.Id == groupId, ct);
        if (group.DialogId is { } dialogId)
        {
            var rows = await db.DirectMessages.AsNoTracking().Where(m => m.DialogId == dialogId && ids.Contains(m.Id)).ToListAsync(ct);
            return (await directChats.BuildMessageDtosAsync(rows, ct)).ToDictionary(m => m.Id);
        }
        var groupRows = await db.GroupMessages.AsNoTracking().Where(m => m.GroupChatId == group.GroupChatId && ids.Contains(m.Id)).ToListAsync(ct);
        return (await groupChats.BuildMessageDtosAsync(groupRows, ct)).ToDictionary(m => m.Id);
    }

    [HttpGet("welcomes")]
    public Task<ActionResult> Welcomes([FromQuery] Guid deviceId, CancellationToken ct) =>
        Run(async () => Ok(await mls.PendingWelcomesAsync(MeId, deviceId, ct)));

    [HttpPost("welcomes/{welcomeId:guid}/ack")]
    public Task<ActionResult> AckWelcome(Guid welcomeId, CancellationToken ct) => Run(async () =>
    {
        await mls.AckWelcomeAsync(MeId, welcomeId, ct);
        return NoContent();
    });
}
