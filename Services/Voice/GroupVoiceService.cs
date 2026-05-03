using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Voice;

public sealed class GroupVoiceService(
    AppDbContext db,
    LiveKitTokenService tokens,
    IOptions<LiveKitOptions> options,
    ILogger<GroupVoiceService> logger)
{
    private readonly LiveKitOptions _options = options.Value;

    public async Task<GroupVoiceJoinResponse> JoinAsync(Guid groupId, Guid userId, string? clientInfo, CancellationToken ct = default)
    {
        if (!tokens.IsConfigured)
            throw new InvalidOperationException("LiveKit is not configured.");

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException("User not found.");

        if (!await db.GroupChatMembers.AsNoTracking().AnyAsync(m => m.GroupChatId == groupId && m.UserId == userId, ct))
            throw new InvalidOperationException("Group chat not found or access denied.");

        await CleanupStaleParticipantsAsync(groupId, ct);

        var now = DateTime.UtcNow;
        var session = await db.GroupVoiceSessions
            .Where(s => s.GroupChatId == groupId && s.State == GroupVoiceSessionState.Active)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        var isNewSession = false;
        if (session is null)
        {
            isNewSession = true;
            session = new GroupVoiceSession
            {
                GroupChatId = groupId,
                RoomName = LiveKitTokenService.BuildGroupRoomName(groupId),
                StartedByUserId = userId,
                StartedAt = now,
                LastActivityAt = now,
                State = GroupVoiceSessionState.Active
            };
            db.GroupVoiceSessions.Add(session);
        }

        var participant = await db.GroupVoiceParticipants
            .FirstOrDefaultAsync(p => p.SessionId == session.Id && p.UserId == userId, ct);

        if (participant is null)
        {
            participant = new GroupVoiceParticipant
            {
                SessionId = session.Id,
                GroupChatId = groupId,
                UserId = userId,
                JoinedAt = now,
                LastSeenAt = now,
                IsActive = true,
                ClientInfo = NormalizeClientInfo(clientInfo)
            };
            db.GroupVoiceParticipants.Add(participant);
        }
        else
        {
            participant.IsActive = true;
            participant.LeftAt = null;
            participant.LastSeenAt = now;
            participant.ClientInfo = NormalizeClientInfo(clientInfo) ?? participant.ClientInfo;
        }

        session.LastActivityAt = now;
        await db.SaveChangesAsync(ct);

        var state = await GetStateAsync(groupId, userId, skipCleanup: true, ct);
        if (state.SessionId is null)
            throw new InvalidOperationException("Failed to create group voice session.");

        var token = tokens.CreateJoinToken(user, groupId, session.Id, session.RoomName);
        return new GroupVoiceJoinResponse(tokens.Url, session.RoomName, token, session.Id, isNewSession, state);
    }

    public async Task<GroupVoiceStateDto> HeartbeatAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        await EnsureMemberAsync(groupId, userId, ct);

        var session = await db.GroupVoiceSessions
            .Where(s => s.GroupChatId == groupId && s.State == GroupVoiceSessionState.Active)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (session is not null)
        {
            var participant = await db.GroupVoiceParticipants
                .FirstOrDefaultAsync(p => p.SessionId == session.Id && p.UserId == userId && p.IsActive, ct);

            if (participant is not null)
            {
                var now = DateTime.UtcNow;
                participant.LastSeenAt = now;
                session.LastActivityAt = now;
                await db.SaveChangesAsync(ct);
            }
        }

        return await GetStateAsync(groupId, userId, ct: ct);
    }

    public async Task<GroupVoiceStateDto> LeaveAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        await EnsureMemberAsync(groupId, userId, ct);

        var session = await db.GroupVoiceSessions
            .Where(s => s.GroupChatId == groupId && s.State == GroupVoiceSessionState.Active)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (session is not null)
        {
            var participant = await db.GroupVoiceParticipants
                .FirstOrDefaultAsync(p => p.SessionId == session.Id && p.UserId == userId && p.IsActive, ct);

            if (participant is not null)
            {
                var now = DateTime.UtcNow;
                participant.IsActive = false;
                participant.LeftAt = now;
                participant.LastSeenAt = now;
                session.LastActivityAt = now;

                var hasActiveParticipants = await db.GroupVoiceParticipants
                    .AnyAsync(p => p.SessionId == session.Id && p.UserId != userId && p.IsActive, ct);

                if (!hasActiveParticipants)
                    EndSession(session, now);

                await db.SaveChangesAsync(ct);
            }
        }

        return await GetStateAsync(groupId, userId, ct: ct);
    }

    public async Task<GroupVoiceStateDto> EndAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        await EnsureMemberAsync(groupId, userId, ct);

        var session = await db.GroupVoiceSessions
            .Where(s => s.GroupChatId == groupId && s.State == GroupVoiceSessionState.Active)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (session is not null)
        {
            var now = DateTime.UtcNow;
            var participants = await db.GroupVoiceParticipants
                .Where(p => p.SessionId == session.Id && p.IsActive)
                .ToListAsync(ct);

            foreach (var participant in participants)
            {
                participant.IsActive = false;
                participant.LeftAt = now;
                participant.LastSeenAt = now;
            }

            EndSession(session, now);
            await db.SaveChangesAsync(ct);
        }

        return await GetStateAsync(groupId, userId, skipCleanup: true, ct);
    }

    public async Task<GroupVoiceStateDto> GetStateAsync(Guid groupId, Guid userId, bool skipCleanup = false, CancellationToken ct = default)
    {
        await EnsureMemberAsync(groupId, userId, ct);
        if (!skipCleanup)
            await CleanupStaleParticipantsAsync(groupId, ct);

        var session = await db.GroupVoiceSessions
            .AsNoTracking()
            .Where(s => s.GroupChatId == groupId && s.State == GroupVoiceSessionState.Active)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (session is null)
        {
            return new GroupVoiceStateDto(
                groupId,
                false,
                null,
                null,
                null,
                null,
                0,
                Array.Empty<GroupVoiceParticipantDto>());
        }

        var participantRows = await (
            from p in db.GroupVoiceParticipants.AsNoTracking()
            join u in db.Users.AsNoTracking() on p.UserId equals u.Id
            where p.SessionId == session.Id && p.IsActive
            orderby p.JoinedAt, u.UserName
            select new GroupVoiceParticipantDto(
                u.Id,
                u.UserName,
                string.IsNullOrWhiteSpace(u.AvatarUrl) ? $"/avatars/{u.Id}" : u.AvatarUrl,
                p.JoinedAt,
                p.LastSeenAt,
                p.IsActive)
        ).ToListAsync(ct);

        var participants = participantRows
            .GroupBy(p => p.UserId)
            .Select(g => g.OrderByDescending(p => p.LastSeenAt).ThenByDescending(p => p.JoinedAt).First())
            .OrderBy(p => p.JoinedAt)
            .ThenBy(p => p.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GroupVoiceStateDto(
            groupId,
            true,
            session.Id,
            session.RoomName,
            session.StartedByUserId,
            session.StartedAt,
            participants.Count,
            participants);
    }

    public async Task<IReadOnlyList<Guid>> GetGroupMemberIdsAsync(Guid groupId, CancellationToken ct = default) =>
        await db.GroupChatMembers.AsNoTracking()
            .Where(m => m.GroupChatId == groupId)
            .Select(m => m.UserId)
            .ToListAsync(ct);

    private async Task EnsureMemberAsync(Guid groupId, Guid userId, CancellationToken ct)
    {
        if (!await db.GroupChatMembers.AsNoTracking().AnyAsync(m => m.GroupChatId == groupId && m.UserId == userId, ct))
            throw new InvalidOperationException("Group chat not found or access denied.");
    }

    private async Task CleanupStaleParticipantsAsync(Guid groupId, CancellationToken ct)
    {
        var staleSeconds = Math.Clamp(_options.ParticipantStaleSeconds, 30, 600);
        var cutoff = DateTime.UtcNow.AddSeconds(-staleSeconds);
        var now = DateTime.UtcNow;

        var sessions = await db.GroupVoiceSessions
            .Where(s => s.GroupChatId == groupId && s.State == GroupVoiceSessionState.Active)
            .ToListAsync(ct);

        foreach (var session in sessions)
        {
            var staleParticipants = await db.GroupVoiceParticipants
                .Where(p => p.SessionId == session.Id && p.IsActive && p.LastSeenAt < cutoff)
                .ToListAsync(ct);

            foreach (var participant in staleParticipants)
            {
                participant.IsActive = false;
                participant.LeftAt = now;
                participant.LastSeenAt = now;
            }

            var hasActiveParticipants = await db.GroupVoiceParticipants
                .AnyAsync(p => p.SessionId == session.Id && p.IsActive && p.LastSeenAt >= cutoff, ct);

            if (!hasActiveParticipants)
                EndSession(session, now);
        }

        if (db.ChangeTracker.HasChanges())
        {
            logger.LogInformation("Cleaned stale group voice participants for group {GroupId}.", groupId);
            await db.SaveChangesAsync(ct);
        }
    }

    private static void EndSession(GroupVoiceSession session, DateTime now)
    {
        session.State = GroupVoiceSessionState.Ended;
        session.EndedAt = now;
        session.LastActivityAt = now;
    }

    private static string? NormalizeClientInfo(string? clientInfo)
    {
        clientInfo = string.IsNullOrWhiteSpace(clientInfo) ? null : clientInfo.Trim();
        if (clientInfo is { Length: > 256 })
            clientInfo = clientInfo[..256];
        return clientInfo;
    }
}
