using System.Security.Claims;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Options;
using JaeZoo.Server.Services.Voice;
using JaeZoo.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Authorize]
public sealed class GroupVoiceController(
    GroupVoiceService voice,
    IHubContext<ChatHub> hub,
    LiveKitTokenService liveKitTokens,
    IOptions<LiveKitOptions> liveKitOptions,
    ILogger<GroupVoiceController> logger) : ControllerBase
{
    private readonly LiveKitOptions _liveKit = liveKitOptions.Value;

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
        => Ok(BuildDiagnosticsPayload(includeRoutes: true));

    [AllowAnonymous]
    [HttpGet("/api/groupvoice/diagnostics")]
    public ActionResult<object> Diagnostics()
        => Ok(BuildDiagnosticsPayload(includeRoutes: true));

    [HttpGet("/api/chat/groups/{groupId:guid}/voice/diagnostics")]
    [HttpGet("/api/groups/{groupId:guid}/voice/diagnostics")]
    public async Task<ActionResult<object>> GroupDiagnostics(Guid groupId, CancellationToken ct)
    {
        var userId = MeId;
        var memberIds = await voice.GetGroupMemberIdsAsync(groupId, ct);
        var state = memberIds.Contains(userId)
            ? await voice.GetStateAsync(groupId, userId, ct: ct)
            : null;

        return Ok(new
        {
            ok = true,
            feature = "group-voice-livekit",
            liveKit = BuildLiveKitDiagnostics(),
            groupId,
            isMember = memberIds.Contains(userId),
            memberCount = memberIds.Count,
            state
        });
    }

    [HttpGet("/api/chat/groups/{groupId:guid}/voice/state")]
    [HttpGet("/api/groups/{groupId:guid}/voice/state")]
    public async Task<ActionResult<GroupVoiceStateDto>> State(Guid groupId, CancellationToken ct)
    {
        var userId = MeId;
        logger.LogInformation("GroupVoice state.begin group={GroupId} user={UserId}", groupId, userId);
        try
        {
            var state = await voice.GetStateAsync(groupId, userId, ct: ct);
            logger.LogInformation("GroupVoice state.ok group={GroupId} user={UserId} active={Active} session={SessionId} participants={Participants}", groupId, userId, state.IsActive, state.SessionId, state.ActiveParticipantCount);
            return Ok(state);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "GroupVoice state.failed group={GroupId} user={UserId}", groupId, userId);
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("/api/chat/groups/{groupId:guid}/voice/join")]
    [HttpPost("/api/groups/{groupId:guid}/voice/join")]
    [RequireVerifiedEmail]
    public async Task<ActionResult<GroupVoiceJoinResponse>> Join(Guid groupId, [FromBody] GroupVoiceJoinRequest? request, CancellationToken ct)
    {
        var userId = MeId;
        logger.LogInformation("GroupVoice join.begin group={GroupId} user={UserId} liveKitConfigured={Configured} clientInfo={ClientInfo}", groupId, userId, liveKitTokens.IsConfigured, request?.ClientInfo);
        try
        {
            var response = await voice.JoinAsync(groupId, userId, request?.ClientInfo, ct);
            logger.LogInformation("GroupVoice join.ok group={GroupId} user={UserId} session={SessionId} room={Room} new={IsNew} participants={Participants}", groupId, userId, response.SessionId, response.RoomName, response.IsNewSession, response.State.ActiveParticipantCount);
            await BroadcastStateAsync(groupId, response.State, response.IsNewSession ? "GroupVoiceStarted" : "GroupVoiceJoined", ct);
            await BroadcastParticipantAsync(groupId, response.SessionId, userId, response.State, "GroupVoiceParticipantJoined", ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "GroupVoice join.failed group={GroupId} user={UserId} configured={Configured}", groupId, userId, liveKitTokens.IsConfigured);
            return BadRequest(new { error = ex.Message, diagnostics = BuildLiveKitDiagnostics() });
        }
    }

    [HttpPost("/api/chat/groups/{groupId:guid}/voice/heartbeat")]
    [HttpPost("/api/groups/{groupId:guid}/voice/heartbeat")]
    public async Task<ActionResult<GroupVoiceStateDto>> Heartbeat(Guid groupId, CancellationToken ct)
    {
        var userId = MeId;
        try
        {
            var state = await voice.HeartbeatAsync(groupId, userId, ct);
            logger.LogDebug("GroupVoice heartbeat.ok group={GroupId} user={UserId} active={Active} participants={Participants}", groupId, userId, state.IsActive, state.ActiveParticipantCount);
            return Ok(state);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "GroupVoice heartbeat.failed group={GroupId} user={UserId}", groupId, userId);
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("/api/chat/groups/{groupId:guid}/voice/leave")]
    [HttpPost("/api/groups/{groupId:guid}/voice/leave")]
    public async Task<ActionResult<GroupVoiceStateDto>> Leave(Guid groupId, CancellationToken ct)
    {
        var userId = MeId;
        logger.LogInformation("GroupVoice leave.begin group={GroupId} user={UserId}", groupId, userId);
        try
        {
            var state = await voice.LeaveAsync(groupId, userId, ct);
            logger.LogInformation("GroupVoice leave.ok group={GroupId} user={UserId} active={Active} session={SessionId} participants={Participants}", groupId, userId, state.IsActive, state.SessionId, state.ActiveParticipantCount);
            if (state.IsActive)
            {
                await BroadcastParticipantAsync(groupId, state.SessionId ?? Guid.Empty, userId, state, "GroupVoiceParticipantLeft", ct);
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
            logger.LogWarning(ex, "GroupVoice leave.failed group={GroupId} user={UserId}", groupId, userId);
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("/api/chat/groups/{groupId:guid}/voice/end")]
    [HttpPost("/api/groups/{groupId:guid}/voice/end")]
    public async Task<ActionResult<GroupVoiceStateDto>> End(Guid groupId, CancellationToken ct)
    {
        var userId = MeId;
        logger.LogInformation("GroupVoice end.begin group={GroupId} user={UserId}", groupId, userId);
        try
        {
            var state = await voice.EndAsync(groupId, userId, ct);
            logger.LogInformation("GroupVoice end.ok group={GroupId} user={UserId} active={Active} session={SessionId}", groupId, userId, state.IsActive, state.SessionId);
            await BroadcastStateAsync(groupId, state, "GroupVoiceEnded", ct);
            return Ok(state);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "GroupVoice end.failed group={GroupId} user={UserId}", groupId, userId);
            return NotFound(new { error = ex.Message });
        }
    }

    private async Task BroadcastStateAsync(Guid groupId, GroupVoiceStateDto state, string eventName, CancellationToken ct)
    {
        var memberIds = await voice.GetGroupMemberIdsAsync(groupId, ct);
        var payload = ToRealtimePayload(groupId, state);
        logger.LogInformation("GroupVoice broadcast.state event={Event} group={GroupId} session={SessionId} recipients={Recipients} active={Active} participants={Participants}", eventName, groupId, state.SessionId, memberIds.Count, state.IsActive, state.ActiveParticipantCount);
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
        logger.LogInformation("GroupVoice broadcast.participant event={Event} group={GroupId} session={SessionId} user={UserId} recipients={Recipients}", eventName, groupId, sessionId, userId, memberIds.Count);
        foreach (var memberId in memberIds)
            await hub.Clients.User(memberId.ToString()).SendAsync(eventName, payload, ct);
    }

    private object BuildDiagnosticsPayload(bool includeRoutes)
        => new
        {
            ok = true,
            feature = "group-voice-livekit",
            version = "stage2-diagnostics-livekit",
            liveKit = BuildLiveKitDiagnostics(),
            serverUtc = DateTime.UtcNow,
            routes = includeRoutes ? new[]
            {
                "/api/groupvoice/ping",
                "/api/groupvoice/diagnostics",
                "/api/chat/groups/{groupId}/voice/diagnostics",
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
            } : Array.Empty<string>()
        };

    private object BuildLiveKitDiagnostics()
    {
        var apiSecret = _liveKit.ApiSecret ?? string.Empty;
        var apiKey = _liveKit.ApiKey ?? string.Empty;
        return new
        {
            configured = liveKitTokens.IsConfigured,
            url = liveKitTokens.Url,
            hasUrl = !string.IsNullOrWhiteSpace(_liveKit.Url),
            hasApiKey = !string.IsNullOrWhiteSpace(apiKey),
            apiKeyLooksLikePlaceholder = LooksLikePlaceholder(apiKey),
            hasApiSecret = !string.IsNullOrWhiteSpace(apiSecret),
            apiSecretLooksLikePlaceholder = LooksLikePlaceholder(apiSecret),
            tokenTtlMinutes = Math.Clamp(_liveKit.TokenTtlMinutes, 5, 24 * 60),
            participantStaleSeconds = Math.Clamp(_liveKit.ParticipantStaleSeconds, 30, 600)
        };
    }

    private static bool LooksLikePlaceholder(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Contains("SET_", StringComparison.OrdinalIgnoreCase)
            || value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || value.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
            || value.Contains("<secret", StringComparison.OrdinalIgnoreCase);
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
