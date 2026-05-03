using System.Security.Claims;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Voice;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Authorize]
public sealed class GroupVoiceController(
    GroupVoiceService voice,
    IHubContext<ChatHub> hub,
    ILogger<GroupVoiceController> logger) : ControllerBase
{
    private Guid MeId
    {
        get
        {
            var s = User.FindFirst("sub")?.Value
                    ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst("uid")?.Value;

            if (!Guid.TryParse(s, out var id))
                throw new UnauthorizedAccessException("No user id claim.");

            return id;
        }
    }

    [AllowAnonymous]
    [HttpGet("/api/groupvoice/ping")]
    public ActionResult<object> Ping()
        => Ok(new
        {
            ok = true,
            feature = "group-voice-livekit",
            version = "fix5-group-voice-end-flat-events",
            routes = new[]
            {
                "/api/chat/groups/{groupId}/voice/state",
                "/api/chat/groups/{groupId}/voice/join",
                "/api/chat/groups/{groupId}/voice/heartbeat",
                "/api/chat/groups/{groupId}/voice/leave",
                "/api/chat/groups/{groupId}/voice/end",
                "/api/groups/{groupId}/voice/state",
                "/api/groups/{groupId}/voice/join",
                "/api/groups/{groupId}/voice/heartbeat",
                "/api/groups/{groupId}/voice/leave",
                "/api/groups/{groupId}/voice/end"
            }
        });

    [HttpGet("/api/chat/groups/{groupId:guid}/voice/state")]
    [HttpGet("/api/groups/{groupId:guid}/voice/state")]
    public async Task<ActionResult<GroupVoiceStateDto>> State(Guid groupId, CancellationToken ct)
    {
        try
        {
            return Ok(await voice.GetStateAsync(groupId, MeId, ct: ct));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("/api/chat/groups/{groupId:guid}/voice/join")]
    [HttpPost("/api/groups/{groupId:guid}/voice/join")]
    public async Task<ActionResult<GroupVoiceJoinResponse>> Join(Guid groupId, [FromBody] GroupVoiceJoinRequest? request, CancellationToken ct)
    {
        try
        {
            var response = await voice.JoinAsync(groupId, MeId, request?.ClientInfo, ct);
            await BroadcastStateAsync(groupId, response.State, response.IsNewSession ? "GroupVoiceStarted" : "GroupVoiceJoined", ct);
            await BroadcastParticipantAsync(groupId, response.SessionId, MeId, response.State, "GroupVoiceParticipantJoined", ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Group voice join failed. group={GroupId} user={UserId}", groupId, MeId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("/api/chat/groups/{groupId:guid}/voice/heartbeat")]
    [HttpPost("/api/groups/{groupId:guid}/voice/heartbeat")]
    public async Task<ActionResult<GroupVoiceStateDto>> Heartbeat(Guid groupId, CancellationToken ct)
    {
        try
        {
            var state = await voice.HeartbeatAsync(groupId, MeId, ct);
            return Ok(state);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("/api/chat/groups/{groupId:guid}/voice/leave")]
    [HttpPost("/api/groups/{groupId:guid}/voice/leave")]
    public async Task<ActionResult<GroupVoiceStateDto>> Leave(Guid groupId, CancellationToken ct)
    {
        try
        {
            var state = await voice.LeaveAsync(groupId, MeId, ct);
            if (state.IsActive)
            {
                await BroadcastParticipantAsync(groupId, state.SessionId ?? Guid.Empty, MeId, state, "GroupVoiceParticipantLeft", ct);
                await BroadcastStateAsync(groupId, state, "GroupVoiceLeft", ct);
            }
            else
            {
                await BroadcastStateAsync(groupId, state, "GroupVoiceEnded", ct);
            }

            return Ok(state);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("/api/chat/groups/{groupId:guid}/voice/end")]
    [HttpPost("/api/groups/{groupId:guid}/voice/end")]
    public async Task<ActionResult<GroupVoiceStateDto>> End(Guid groupId, CancellationToken ct)
    {
        try
        {
            var state = await voice.EndAsync(groupId, MeId, ct);
            await BroadcastStateAsync(groupId, state, "GroupVoiceEnded", ct);
            return Ok(state);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    private async Task BroadcastStateAsync(Guid groupId, GroupVoiceStateDto state, string eventName, CancellationToken ct)
    {
        var memberIds = await voice.GetGroupMemberIdsAsync(groupId, ct);
        var payload = ToRealtimePayload(groupId, state);
        foreach (var memberId in memberIds)
        {
            await hub.Clients.User(memberId.ToString()).SendAsync(eventName, payload, ct);
            await hub.Clients.User(memberId.ToString()).SendAsync("GroupVoiceStateChanged", payload, ct);
        }
    }

    private async Task BroadcastParticipantAsync(Guid groupId, Guid sessionId, Guid userId, GroupVoiceStateDto state, string eventName, CancellationToken ct)
    {
        if (sessionId == Guid.Empty)
            return;

        var memberIds = await voice.GetGroupMemberIdsAsync(groupId, ct);
        var payload = ToRealtimePayload(groupId, state, userId);
        foreach (var memberId in memberIds)
            await hub.Clients.User(memberId.ToString()).SendAsync(eventName, payload, ct);
    }

    private static object ToRealtimePayload(Guid groupId, GroupVoiceStateDto state, Guid? userId = null)
        => new
        {
            GroupId = groupId,
            SessionId = state.SessionId,
            IsActive = state.IsActive,
            RoomName = state.RoomName,
            StartedByUserId = state.StartedByUserId,
            StartedAtUtc = state.StartedAt,
            ParticipantCount = state.ActiveParticipantCount,
            UserId = userId,
            Participants = state.Participants
        };
}
