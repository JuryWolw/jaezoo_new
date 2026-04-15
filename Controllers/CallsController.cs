using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Calls;
using JaeZoo.Server.Services.Calls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class CallsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CallSessionService _sessions;
    private readonly TurnCredentialsService _turn;
    private readonly CallAuditService _audit;
    private readonly CallHistoryService _history;
    private readonly IHubContext<CallsHub> _callsHub;

    public CallsController(
        AppDbContext db,
        CallSessionService sessions,
        TurnCredentialsService turn,
        CallAuditService audit,
        CallHistoryService history,
        IHubContext<CallsHub> callsHub)
    {
        _db = db;
        _sessions = sessions;
        _turn = turn;
        _audit = audit;
        _history = history;
        _callsHub = callsHub;
    }

    private Guid MeId
    {
        get
        {
            var id = User.FindFirstValue("sub")
                     ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? User.FindFirstValue("uid");

            if (!Guid.TryParse(id, out var parsed))
                throw new UnauthorizedAccessException("No user id claim.");

            return parsed;
        }
    }

    [HttpGet("ice-config")]
    public ActionResult<IceConfigResponse> GetIceConfig()
        => Ok(_turn.CreateForUser(MeId));

    [HttpGet("active")]
    public ActionResult<IEnumerable<StartCallResponse>> GetActive()
    {
        var data = _sessions.GetActiveForUser(MeId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new StartCallResponse(x.CallId, _sessions.GetPeerId(x, MeId), x.DialogId, x.Type, x.State, x.CreatedAtUtc, x.CorrelationId))
            .ToList();

        return Ok(data);
    }

    [HttpPost("start")]
    public async Task<ActionResult<StartCallResponse>> Start([FromBody] StartCallRequest request, CancellationToken ct)
    {
        var me = MeId;
        if (request.PeerUserId == Guid.Empty || request.PeerUserId == me)
            return BadRequest(new { message = "Invalid peer user id." });

        var peer = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.PeerUserId, ct);
        if (peer is null)
            return NotFound(new { message = "Peer user not found." });

        var areFriends = await _db.Friendships.AsNoTracking().AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == me && f.AddresseeId == request.PeerUserId) ||
             (f.RequesterId == request.PeerUserId && f.AddresseeId == me)), ct);

        if (!areFriends)
            return Forbid();

        var caller = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == me, ct);

        Guid resolvedDialogId;
        try
        {
            resolvedDialogId = await _history.ResolveDirectDialogIdAsync(me, request.PeerUserId, request.DialogId, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        if (!_sessions.TryCreateIfUsersAvailable(me, request.PeerUserId, resolvedDialogId, request.Type, request.ClientVersion, request.DeviceInfo, out var session) || session is null)
            return Conflict(new { message = "Caller or callee already has an active call." });

        _sessions.Update(session.CallId, s =>
        {
            s.State = CallState.Ringing;
            s.LastActivityAtUtc = DateTime.UtcNow;
            s.LastCallerActivityAtUtc = DateTime.UtcNow;
            s.LastCalleeActivityAtUtc = DateTime.UtcNow;
        });

        var invite = new CallInviteDto(
            session.CallId,
            session.CallerUserId,
            session.CalleeUserId,
            session.DialogId,
            session.Type,
            session.CreatedAtUtc,
            session.CorrelationId,
            caller.DisplayName ?? caller.UserName,
            string.IsNullOrWhiteSpace(caller.AvatarUrl) ? $"/avatars/{caller.Id}" : caller.AvatarUrl);

        var changed = new CallStateChangedDto(
            session.CallId,
            session.CallerUserId,
            session.CalleeUserId,
            session.DialogId,
            session.Type,
            session.State,
            DateTime.UtcNow,
            null,
            session.CorrelationId);

        _audit.Info(session, "call.invite.created");

        await _callsHub.Clients.User(request.PeerUserId.ToString()).SendAsync("call.invite", invite, ct);
        await _callsHub.Clients.Users(me.ToString(), request.PeerUserId.ToString()).SendAsync("call.state", changed, ct);

        return Ok(new StartCallResponse(session.CallId, request.PeerUserId, session.DialogId, session.Type, session.State, session.CreatedAtUtc, session.CorrelationId));
    }
}
