using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Security;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace JaeZoo.Server.Services.Chat;

public sealed class GroupChatService(AppDbContext db, DirectChatService directChat)
{
    public const int MaxGroupMembers = 50;

    public static void AdvanceSecurityEpoch(GroupChat chat, DateTime now)
    {
        chat.SecurityEpoch = Math.Max(1, chat.SecurityEpoch) + 1;
        chat.SecurityEpochChangedAt = now;
        chat.UpdatedAt = now;
    }

    private static bool IsGroupE2eePayload(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.StartsWith(E2eeEnvelopeInspector.GroupPrefixV1, StringComparison.Ordinal);

    private static void ValidateGroupE2eePayload(Guid groupId, int currentEpoch, string? text)
    {
        if (!IsGroupE2eePayload(text))
            return;

        try
        {
            var base64 = text![E2eeEnvelopeInspector.GroupPrefixV1.Length..];
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var payload = JsonSerializer.Deserialize<GroupE2eeServerEnvelope>(json);
            if (payload is null)
                throw new InvalidOperationException("Invalid group E2EE envelope.");
            if (payload.groupId != groupId)
                throw new InvalidOperationException("Group E2EE envelope belongs to another group.");
            if (payload.securityEpoch != currentEpoch)
                throw new InvalidOperationException("Group E2EE keys are stale. Refresh the group and send the message again.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Invalid group E2EE envelope.", ex);
        }
    }

    private sealed record GroupE2eeServerEnvelope(int v, Guid groupId, int securityEpoch);

    public static string GetAvatarUrl(GroupChat chat) =>
        string.IsNullOrWhiteSpace(chat.AvatarUrl) ? $"/avatars/groups/{chat.Id}" : chat.AvatarUrl;

    private static string? NormalizeDescription(string? description)
    {
        var normalized = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (normalized is not null && normalized.Length > 1000)
            throw new InvalidOperationException("Description is too long.");
        return normalized;
    }

    private static int NormalizeHistoryPolicy(int? historyPolicy)
    {
        // 0 = новые участники видят только новые сообщения.
        // 1 = новые участники смогут получить старую историю, если админ передаст её в E9.2.
        // 2 = история явно закрыта для новых участников. Пока в UI не выводится.
        var value = historyPolicy ?? 1;
        return value switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            _ => 1
        };
    }

    private static GroupChatRole GetEffectiveRole(GroupChat chat, GroupChatMember member) =>
        chat.OwnerId == member.UserId ? GroupChatRole.Admin : member.Role;

    private static bool CanEditGroup(GroupChat chat, GroupChatMember member) =>
        chat.OwnerId == member.UserId || member.Role == GroupChatRole.Admin;

    private static bool CanManageRoles(GroupChat chat, GroupChatMember member) =>
        chat.OwnerId == member.UserId || member.Role == GroupChatRole.Admin;

    private static bool CanRemoveMember(GroupChat chat, GroupChatMember actor, GroupChatMember target)
    {
        if (target.UserId == chat.OwnerId)
            return false;

        if (chat.OwnerId == actor.UserId || actor.Role == GroupChatRole.Admin)
            return true;

        if (actor.Role == GroupChatRole.Moderator)
            return target.Role is GroupChatRole.Member or GroupChatRole.Helper;

        return false;
    }

    public async Task<bool> IsMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default) =>
        await db.GroupChatMembers.AsNoTracking().AnyAsync(m => m.GroupChatId == groupId && m.UserId == userId, ct);

    public async Task<GroupChat?> GetChatForMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var isMember = await IsMemberAsync(groupId, userId, ct);
        if (!isMember)
            return null;

        return await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct);
    }

    public async Task<GroupChat> CreateChatAsync(Guid ownerId, string? title, string? description, IReadOnlyCollection<Guid>? memberIds, bool isPublic = false, int historyPolicy = 1, CancellationToken ct = default)
    {
        var normalizedTitle = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            throw new InvalidOperationException("Title is required.");
        if (normalizedTitle.Length > 120)
            throw new InvalidOperationException("Title is too long.");
        var normalizedDescription = NormalizeDescription(description);

        var requested = (memberIds ?? Array.Empty<Guid>())
            .Where(x => x != Guid.Empty && x != ownerId)
            .Distinct()
            .ToList();

        if (requested.Count + 1 > MaxGroupMembers)
            throw new InvalidOperationException($"Group member limit is {MaxGroupMembers}.");

        if (requested.Count > 0)
        {
            var acceptedFriendIds = await db.Friendships
                .AsNoTracking()
                .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == ownerId || f.AddresseeId == ownerId))
                .Select(f => f.RequesterId == ownerId ? f.AddresseeId : f.RequesterId)
                .Distinct()
                .ToListAsync(ct);

            var friendSet = acceptedFriendIds.ToHashSet();
            if (requested.Any(id => !friendSet.Contains(id)))
                throw new InvalidOperationException("You can add only accepted friends when creating a group.");
        }

        var existingUsers = await db.Users
            .AsNoTracking()
            .Where(u => requested.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(ct);

        if (existingUsers.Count != requested.Count)
            throw new InvalidOperationException("One or more selected users were not found.");

        var now = DateTime.UtcNow;
        var chat = new GroupChat
        {
            Title = normalizedTitle,
            Description = normalizedDescription,
            OwnerId = ownerId,
            MemberLimit = MaxGroupMembers,
            IsPublic = isPublic,
            HistoryPolicy = NormalizeHistoryPolicy(historyPolicy),
            HistoryPolicyChangedAt = now,
            SecurityEpoch = 1,
            SecurityEpochChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.GroupChats.Add(chat);
        db.GroupChatMembers.Add(new GroupChatMember
        {
            GroupChatId = chat.Id,
            UserId = ownerId,
            Role = GroupChatRole.Admin,
            JoinedAt = now
        });

        foreach (var memberId in requested)
        {
            db.GroupChatMembers.Add(new GroupChatMember
            {
                GroupChatId = chat.Id,
                UserId = memberId,
                JoinedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        return chat;
    }

    public async Task<GroupChat> UpdateChatAsync(Guid groupId, Guid me, string? title, string? description, bool? isPublic = null, int? historyPolicy = null, CancellationToken ct = default)
    {
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new InvalidOperationException("Group chat not found.");

        var actor = await db.GroupChatMembers.FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == me, ct)
            ?? throw new InvalidOperationException("Group membership not found.");

        if (!CanEditGroup(chat, actor))
            throw new InvalidOperationException("Only the owner or admin can edit the group.");

        var normalizedTitle = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            throw new InvalidOperationException("Title is required.");
        if (normalizedTitle.Length > 120)
            throw new InvalidOperationException("Title is too long.");

        chat.Title = normalizedTitle;
        chat.Description = NormalizeDescription(description);
        if (isPublic.HasValue)
            chat.IsPublic = isPublic.Value;
        if (historyPolicy.HasValue)
        {
            var normalizedPolicy = NormalizeHistoryPolicy(historyPolicy.Value);
            if (chat.HistoryPolicy != normalizedPolicy)
            {
                chat.HistoryPolicy = normalizedPolicy;
                chat.HistoryPolicyChangedAt = DateTime.UtcNow;
            }
        }
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return chat;
    }

    public async Task<GroupChat> UpdateAvatarUrlAsync(Guid groupId, Guid me, string? avatarUrl, CancellationToken ct = default)
    {
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new InvalidOperationException("Group chat not found.");

        var actor = await db.GroupChatMembers.FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == me, ct)
            ?? throw new InvalidOperationException("Group membership not found.");

        if (!CanEditGroup(chat, actor))
            throw new InvalidOperationException("Only the owner or admin can change the group avatar.");

        chat.AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim();
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return chat;
    }

    public async Task<IReadOnlyList<GroupChatMember>> AddMembersAsync(Guid groupId, Guid me, IReadOnlyCollection<Guid>? userIds, CancellationToken ct = default)
    {
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new InvalidOperationException("Group chat not found.");

        var actor = await db.GroupChatMembers
            .FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == me, ct)
            ?? throw new InvalidOperationException("Only group members can add members.");

        if (!CanEditGroup(chat, actor))
            throw new InvalidOperationException("Only the owner or admin can add members.");

        var requested = (userIds ?? Array.Empty<Guid>())
            .Where(x => x != Guid.Empty)
            .Distinct()
            .Where(x => x != me)
            .ToList();

        if (requested.Count == 0)
            throw new InvalidOperationException("UserIds are required.");

        var currentMembers = await db.GroupChatMembers
            .Where(m => m.GroupChatId == groupId)
            .ToListAsync(ct);

        var currentIds = currentMembers.Select(m => m.UserId).ToHashSet();
        requested = requested.Where(id => !currentIds.Contains(id)).ToList();
        if (requested.Count == 0)
            return currentMembers;

        if (currentMembers.Count + requested.Count > chat.MemberLimit)
            throw new InvalidOperationException($"Group member limit is {chat.MemberLimit}.");

        var acceptedFriendIds = await db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == me || f.AddresseeId == me))
            .Select(f => f.RequesterId == me ? f.AddresseeId : f.RequesterId)
            .Distinct()
            .ToListAsync(ct);

        var friendSet = acceptedFriendIds.ToHashSet();
        if (requested.Any(id => !friendSet.Contains(id)))
            throw new InvalidOperationException("You can add only your accepted friends.");

        var existingUsers = await db.Users.AsNoTracking().Where(u => requested.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        if (existingUsers.Count != requested.Count)
            throw new InvalidOperationException("One or more selected users were not found.");

        var now = DateTime.UtcNow;
        foreach (var userId in requested)
        {
            db.GroupChatMembers.Add(new GroupChatMember
            {
                GroupChatId = groupId,
                UserId = userId,
                JoinedAt = now
            });
        }

        AdvanceSecurityEpoch(chat, now);
        await db.SaveChangesAsync(ct);

        return await db.GroupChatMembers
            .AsNoTracking()
            .Where(m => m.GroupChatId == groupId)
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GroupChatMember>> RemoveMemberAsync(Guid groupId, Guid me, Guid userId, CancellationToken ct = default)
    {
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new InvalidOperationException("Group chat not found.");

        var actor = await db.GroupChatMembers.FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == me, ct)
            ?? throw new InvalidOperationException("Group membership not found.");
        var member = await db.GroupChatMembers.FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == userId, ct);
        if (member is null)
            throw new InvalidOperationException("Member was not found.");

        if (!CanRemoveMember(chat, actor, member))
            throw new InvalidOperationException("You do not have access to remove this member.");

        db.GroupChatMembers.Remove(member);
        AdvanceSecurityEpoch(chat, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);

        return await db.GroupChatMembers
            .AsNoTracking()
            .Where(m => m.GroupChatId == groupId)
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);
    }


    public async Task<IReadOnlyList<GroupChatMember>> UpdateMemberRoleAsync(Guid groupId, Guid me, Guid userId, GroupChatRole role, CancellationToken ct = default)
    {
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new InvalidOperationException("Group chat not found.");

        var actor = await db.GroupChatMembers.FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == me, ct)
            ?? throw new InvalidOperationException("Group membership not found.");
        if (!CanManageRoles(chat, actor))
            throw new InvalidOperationException("Only the owner or admin can manage roles.");
        if (userId == chat.OwnerId)
            throw new InvalidOperationException("Owner role cannot be changed.");

        var target = await db.GroupChatMembers.FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == userId, ct)
            ?? throw new InvalidOperationException("Member was not found.");

        target.Role = role;
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return await db.GroupChatMembers
            .AsNoTracking()
            .Where(m => m.GroupChatId == groupId)
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);
    }

    public async Task LeaveAsync(Guid groupId, Guid me, CancellationToken ct = default)
    {
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new InvalidOperationException("Group chat not found.");

        if (chat.OwnerId == me)
            throw new InvalidOperationException("Owner cannot leave the group. Transfer ownership is not implemented yet.");

        var member = await db.GroupChatMembers.FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == me, ct);
        if (member is null)
            throw new InvalidOperationException("You are not a member of this group.");

        db.GroupChatMembers.Remove(member);
        AdvanceSecurityEpoch(chat, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    public async Task<(int count, Guid? firstId, DateTime? firstAt)> GetUnreadForUserAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var member = await db.GroupChatMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == userId, ct);

        if (member is null)
            return (0, null, null);

        var at = DirectChatService.EnsureUtc(member.LastReadAt);
        var id = member.LastReadMessageId;

        var q = db.GroupMessages
            .AsNoTracking()
            .Where(m => m.GroupChatId == groupId && m.SenderId != userId && m.DeletedAt == null)
            .Where(m => m.SentAt > at || (m.SentAt == at && m.Id.CompareTo(id) > 0));

        var count = await q.CountAsync(ct);
        if (count == 0)
            return (0, null, null);

        var first = await q.OrderBy(m => m.SentAt).ThenBy(m => m.Id).Select(m => new { m.Id, m.SentAt }).FirstOrDefaultAsync(ct);
        return (count, first?.Id, first?.SentAt);
    }

    public async Task<List<GroupChatMemberDto>> GetMemberDtosAsync(Guid groupId, CancellationToken ct = default)
    {
        var rows = await (
            from m in db.GroupChatMembers.AsNoTracking()
            join u in db.Users.AsNoTracking() on m.UserId equals u.Id
            join g in db.GroupChats.AsNoTracking() on m.GroupChatId equals g.Id
            where m.GroupChatId == groupId
            orderby m.JoinedAt, m.Id
            select new { Member = m, User = u, Group = g }
        ).ToListAsync(ct);

        return rows.Select(x =>
        {
            var role = x.Group.OwnerId == x.User.Id ? GroupChatRole.Admin : x.Member.Role;
            var publicName = UserIdentityService.GetPublicName(x.User);
            return new GroupChatMemberDto(
                x.User.Id,
                publicName,
                string.Empty,
                UserIdentityService.GetAvatarUrl(x.User),
                x.Member.JoinedAt,
                x.Group.OwnerId == x.User.Id,
                role,
                GroupChatRoleInfo.GetDisplayName(role),
                GroupChatRoleInfo.GetColorHex(role),
                GroupChatRoleInfo.GetColorName(role),
                publicName,
                x.User.PublicId);
        }).ToList();
    }

    public async Task<GroupChatSummaryDto?> GetSummaryAsync(Guid groupId, Guid me, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(groupId, me, ct))
            return null;

        var chat = await db.GroupChats.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (chat is null)
            return null;

        var member = await db.GroupChatMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == me, ct);
        if (member is null)
            return null;

        var myRole = GetEffectiveRole(chat, member);
        var memberCount = await db.GroupChatMembers.AsNoTracking().CountAsync(m => m.GroupChatId == groupId, ct);
        var lastMessage = await db.GroupMessages
            .AsNoTracking()
            .Where(m => m.GroupChatId == groupId)
            .OrderByDescending(m => m.SentAt)
            .ThenByDescending(m => m.Id)
            .FirstOrDefaultAsync(ct);

        MessageDto? lastMessageDto = null;
        if (lastMessage is not null)
            lastMessageDto = await GetMessageDtoAsync(groupId, lastMessage.Id, ct);

        var unread = await GetUnreadForUserAsync(groupId, me, ct);
        return new GroupChatSummaryDto(
            chat.Id,
            chat.Title,
            chat.Description,
            GetAvatarUrl(chat),
            chat.OwnerId,
            memberCount,
            chat.MemberLimit,
            chat.CreatedAt,
            chat.UpdatedAt,
            lastMessage?.SentAt,
            lastMessageDto,
            myRole,
            GroupChatRoleInfo.GetDisplayName(myRole),
            GroupChatRoleInfo.GetColorHex(myRole),
            GroupChatRoleInfo.GetColorName(myRole),
            CanEditGroup(chat, member),
            CanEditGroup(chat, member),
            CanManageRoles(chat, member),
            unread.count,
            unread.firstId,
            unread.firstAt,
            Math.Max(1, chat.SecurityEpoch),
            chat.SecurityEpochChangedAt,
            chat.IsPublic,
            true,
            chat.HistoryPolicy,
            chat.HistoryPolicyChangedAt);
    }

    public async Task<List<GroupChatSummaryDto>> ListForUserAsync(Guid me, CancellationToken ct = default)
    {
        var groupIds = await db.GroupChatMembers
            .AsNoTracking()
            .Where(m => m.UserId == me)
            .OrderByDescending(m => m.JoinedAt)
            .Select(m => m.GroupChatId)
            .Distinct()
            .ToListAsync(ct);

        var result = new List<GroupChatSummaryDto>(groupIds.Count);
        foreach (var groupId in groupIds)
        {
            var summary = await GetSummaryAsync(groupId, me, ct);
            if (summary is not null)
                result.Add(summary);
        }

        return result
            .OrderByDescending(x => x.LastMessageAt ?? x.UpdatedAt)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    public async Task<List<PublicGroupSearchDto>> SearchPublicAsync(Guid me, string? query, int take = 30, CancellationToken ct = default)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length < 2)
            return new List<PublicGroupSearchDto>();

        take = Math.Clamp(take, 1, 50);

        var memberGroupIds = await db.GroupChatMembers
            .AsNoTracking()
            .Where(m => m.UserId == me)
            .Select(m => m.GroupChatId)
            .ToListAsync(ct);
        var memberSet = memberGroupIds.ToHashSet();

        var lower = q.ToLowerInvariant();
        var hasGuid = Guid.TryParse(q, out var queryGuid);
        var groups = await db.GroupChats
            .AsNoTracking()
            .Where(g => g.IsPublic &&
                ((hasGuid && g.Id == queryGuid) || g.Title.ToLower().Contains(lower) || (g.Description != null && g.Description.ToLower().Contains(lower))))
            .OrderBy(g => hasGuid && g.Id == queryGuid ? 0 : 1)
            .ThenBy(g => g.Title)
            .Take(take)
            .ToListAsync(ct);

        var ids = groups.Select(g => g.Id).ToList();
        var counts = await db.GroupChatMembers
            .AsNoTracking()
            .Where(m => ids.Contains(m.GroupChatId))
            .GroupBy(m => m.GroupChatId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GroupId, x => x.Count, ct);

        return groups.Select(g => new PublicGroupSearchDto(
            g.Id,
            g.Title,
            g.Description,
            GetAvatarUrl(g),
            g.OwnerId,
            counts.TryGetValue(g.Id, out var c) ? c : 0,
            g.MemberLimit,
            g.CreatedAt,
            g.UpdatedAt,
            g.IsPublic,
            memberSet.Contains(g.Id))).ToList();
    }

    public async Task<GroupChat> JoinPublicAsync(Guid groupId, Guid me, CancellationToken ct = default)
    {
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new InvalidOperationException("Group chat not found.");

        if (!chat.IsPublic)
            throw new InvalidOperationException("This group is private. You can join it only by invite.");

        var isAlreadyMember = await db.GroupChatMembers.AnyAsync(m => m.GroupChatId == groupId && m.UserId == me, ct);
        if (isAlreadyMember)
            return chat;

        var count = await db.GroupChatMembers.CountAsync(m => m.GroupChatId == groupId, ct);
        if (count >= chat.MemberLimit)
            throw new InvalidOperationException($"Group member limit is {chat.MemberLimit}.");

        var now = DateTime.UtcNow;
        db.GroupChatMembers.Add(new GroupChatMember
        {
            GroupChatId = groupId,
            UserId = me,
            JoinedAt = now
        });
        AdvanceSecurityEpoch(chat, now);
        await db.SaveChangesAsync(ct);
        return chat;
    }

    public AttachmentDto ToAttachmentDto(ChatFile f) => directChat.ToAttachmentDto(f);

    public async Task<Dictionary<Guid, List<AttachmentDto>>> LoadAttachmentsForMessagesAsync(List<Guid> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0)
            return new();

        var rows = await (
            from a in db.GroupMessageAttachments.AsNoTracking()
            join f in db.ChatFiles.AsNoTracking() on a.FileId equals f.Id
            where messageIds.Contains(a.MessageId)
                  && f.DeletedAt == null
                  && f.BlockedAt == null
            orderby a.CreatedAt, a.Id
            select new { a.MessageId, File = f }
        ).ToListAsync(ct);

        var map = new Dictionary<Guid, List<AttachmentDto>>();
        foreach (var r in rows)
        {
            if (!map.TryGetValue(r.MessageId, out var list))
            {
                list = new List<AttachmentDto>();
                map[r.MessageId] = list;
            }
            list.Add(ToAttachmentDto(r.File));
        }

        return map;
    }

    public async Task<Dictionary<Guid, MessageForwardInfoDto>> LoadForwardInfosAsync(List<GroupMessage> messages, CancellationToken ct)
    {
        var sourceIds = messages.Where(m => m.ForwardedFromMessageId.HasValue).Select(m => m.ForwardedFromMessageId!.Value).Distinct().ToList();
        if (sourceIds.Count == 0)
            return new();

        var result = new Dictionary<Guid, MessageForwardInfoDto>();

        var groupSources = await db.GroupMessages
            .AsNoTracking()
            .Where(m => sourceIds.Contains(m.Id))
            .Select(m => new { m.Id, m.SenderId, m.Text, m.SentAt, m.Kind, m.SystemKey, m.DeletedAt })
            .ToListAsync(ct);

        var groupAttachmentMessageIds = await db.GroupMessageAttachments
            .AsNoTracking()
            .Where(a => sourceIds.Contains(a.MessageId))
            .Select(a => a.MessageId)
            .Distinct()
            .ToListAsync(ct);
        var groupHasAttachments = groupAttachmentMessageIds.ToHashSet();

        foreach (var x in groupSources)
        {
            result[x.Id] = new MessageForwardInfoDto(
                x.Id,
                x.SenderId,
                x.DeletedAt.HasValue ? string.Empty : x.Text,
                x.SentAt,
                groupHasAttachments.Contains(x.Id) && !x.DeletedAt.HasValue,
                x.Kind,
                x.DeletedAt.HasValue ? null : x.SystemKey,
                x.DeletedAt);
        }

        var unresolvedIds = sourceIds.Where(id => !result.ContainsKey(id)).ToList();
        if (unresolvedIds.Count > 0)
        {
            var directSources = await db.DirectMessages
                .AsNoTracking()
                .Where(m => unresolvedIds.Contains(m.Id))
                .Select(m => new { m.Id, m.SenderId, m.Text, m.SentAt, m.Kind, m.SystemKey, m.DeletedAt })
                .ToListAsync(ct);

            var directAttachmentMessageIds = await db.DirectMessageAttachments
                .AsNoTracking()
                .Where(a => unresolvedIds.Contains(a.MessageId))
                .Select(a => a.MessageId)
                .Distinct()
                .ToListAsync(ct);
            var directHasAttachments = directAttachmentMessageIds.ToHashSet();

            foreach (var x in directSources)
            {
                result[x.Id] = new MessageForwardInfoDto(
                    x.Id,
                    x.SenderId,
                    x.DeletedAt.HasValue ? string.Empty : x.Text,
                    x.SentAt,
                    directHasAttachments.Contains(x.Id) && !x.DeletedAt.HasValue,
                    x.Kind,
                    x.DeletedAt.HasValue ? null : x.SystemKey,
                    x.DeletedAt);
            }
        }

        return result;
    }

    public MessageDto ToMessageDto(GroupMessage message, IReadOnlyList<AttachmentDto>? attachments = null, MessageForwardInfoDto? forwardedFrom = null)
    {
        var dtoAttachments = message.DeletedAt.HasValue ? Array.Empty<AttachmentDto>() : attachments;
        var dtoText = message.DeletedAt.HasValue ? string.Empty : message.Text;

        return new MessageDto(
            message.Id,
            message.SenderId,
            dtoText,
            message.SentAt,
            dtoAttachments,
            message.Kind,
            message.SystemKey,
            message.ForwardedFromMessageId,
            message.EditedAt,
            message.DeletedAt,
            message.DeletedById,
            forwardedFrom,
            Math.Max(1, message.GroupSecurityEpoch));
    }

    public async Task<List<MessageDto>> BuildMessageDtosAsync(IReadOnlyList<GroupMessage> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0)
            return new();

        var ids = messages.Select(m => m.Id).ToList();
        var attachments = await LoadAttachmentsForMessagesAsync(ids, ct);
        var forwards = await LoadForwardInfosAsync(messages.ToList(), ct);

        return messages.Select(m => ToMessageDto(
            m,
            attachments.TryGetValue(m.Id, out var att) ? att : null,
            m.ForwardedFromMessageId.HasValue && forwards.TryGetValue(m.ForwardedFromMessageId.Value, out var fwd) ? fwd : null)).ToList();
    }

    public async Task<MessageDto?> GetMessageDtoAsync(Guid groupId, Guid messageId, CancellationToken ct = default)
    {
        var message = await db.GroupMessages.AsNoTracking().FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.Id == messageId, ct);
        if (message is null)
            return null;

        var dtos = await BuildMessageDtosAsync(new[] { message }, ct);
        return dtos[0];
    }

    public async Task<(GroupChat chat, GroupMessage message, List<ChatFile> files)> CreateMessageAsync(
        Guid senderId,
        Guid groupId,
        string? text,
        IReadOnlyCollection<Guid>? fileIds,
        DirectMessageKind kind,
        string? systemKey,
        Guid? forwardedFromMessageId,
        CancellationToken ct = default)
    {
        var chat = await GetChatForMemberAsync(groupId, senderId, ct)
            ?? throw new InvalidOperationException("Group chat not found or access denied.");

        text = (text ?? string.Empty).Trim();
        if (kind == DirectMessageKind.User)
            ValidateGroupE2eePayload(groupId, Math.Max(1, chat.SecurityEpoch), text);

        var ids = (fileIds ?? Array.Empty<Guid>())
            .Where(x => x != Guid.Empty)
            .Distinct()
            .Take(10)
            .ToList();

        var hasSystemMarker = kind == DirectMessageKind.System && !string.IsNullOrWhiteSpace(systemKey);
        if (string.IsNullOrWhiteSpace(text) && ids.Count == 0 && !hasSystemMarker)
            throw new InvalidOperationException("Message must contain text or attachments.");

        var files = new List<ChatFile>();
        if (ids.Count > 0)
        {
            files = await db.ChatFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
            if (files.Count != ids.Count)
                throw new InvalidOperationException("One or more files were not found.");

            foreach (var f in files)
            {
                if (f.UploaderId != senderId)
                    throw new InvalidOperationException("Cannot attach someone else's file.");
                if (f.IsAttached)
                    throw new InvalidOperationException("One or more files are already attached.");
            }
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var message = new GroupMessage
        {
            GroupChatId = groupId,
            SenderId = senderId,
            Text = text,
            SentAt = now,
            E2eeEnvelopeVersion = E2eeEnvelopeInspector.InspectGroup(text).Version,
            E2eeProtocol = E2eeEnvelopeInspector.InspectGroup(text).Protocol,
            GroupSecurityEpoch = Math.Max(1, chat.SecurityEpoch),
            Kind = kind,
            SystemKey = string.IsNullOrWhiteSpace(systemKey) ? null : systemKey.Trim(),
            ForwardedFromMessageId = forwardedFromMessageId
        };
        db.GroupMessages.Add(message);

        foreach (var f in files)
        {
            db.GroupMessageAttachments.Add(new GroupMessageAttachment
            {
                MessageId = message.Id,
                FileId = f.Id,
                CreatedAt = now
            });

            f.IsAttached = true;
            f.AttachedAt = now;
        }

        chat.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (chat, message, files);
    }

    public async Task<(GroupChat chat, GroupMessage message)> ForwardMessageAsync(
        Guid senderId,
        Guid groupId,
        GroupMessage source,
        bool includeAttachments,
        CancellationToken ct = default)
    {
        if (source.DeletedAt.HasValue)
            throw new InvalidOperationException("Deleted message cannot be forwarded.");

        var chat = await GetChatForMemberAsync(groupId, senderId, ct)
            ?? throw new InvalidOperationException("Group chat not found or access denied.");
        var now = DateTime.UtcNow;

        List<ChatFile> sourceFiles = [];
        if (includeAttachments)
        {
            sourceFiles = await (
                from a in db.GroupMessageAttachments
                join f in db.ChatFiles on a.FileId equals f.Id
                where a.MessageId == source.Id
                orderby a.CreatedAt, a.Id
                select f
            ).ToListAsync(ct);
        }

        var hasSystemMarker = source.Kind == DirectMessageKind.System && !string.IsNullOrWhiteSpace(source.SystemKey);
        if (string.IsNullOrWhiteSpace(source.Text) && sourceFiles.Count == 0 && !hasSystemMarker)
            throw new InvalidOperationException("Message must contain text or attachments.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var clones = sourceFiles.Select(f => new ChatFile
        {
            UploaderId = senderId,
            OriginalFileName = f.OriginalFileName,
            SafeFileName = string.IsNullOrWhiteSpace(f.SafeFileName) ? f.OriginalFileName : f.SafeFileName,
            ContentType = f.ContentType,
            DetectedContentType = string.IsNullOrWhiteSpace(f.DetectedContentType) ? f.ContentType : f.DetectedContentType,
            SizeBytes = f.SizeBytes,
            StoredPath = f.StoredPath,
            Bucket = string.IsNullOrWhiteSpace(f.Bucket) ? "jaezoo-files" : f.Bucket,
            ObjectKey = string.IsNullOrWhiteSpace(f.ObjectKey) ? f.StoredPath : f.ObjectKey,
            Sha256 = f.Sha256 ?? string.Empty,
            Kind = f.Kind,
            ScanStatus = f.ScanStatus,
            IsPotentiallyDangerous = f.IsPotentiallyDangerous,
            RiskNote = f.RiskNote,
            CreatedAt = now,
            IsAttached = true,
            AttachedAt = now
        }).ToList();

        if (clones.Count > 0)
            db.ChatFiles.AddRange(clones);

        var message = new GroupMessage
        {
            GroupChatId = groupId,
            SenderId = senderId,
            Text = (source.Text ?? string.Empty).Trim(),
            SentAt = now,
            GroupSecurityEpoch = Math.Max(1, chat.SecurityEpoch),
            Kind = source.Kind,
            SystemKey = string.IsNullOrWhiteSpace(source.SystemKey) ? null : source.SystemKey.Trim(),
            ForwardedFromMessageId = source.Id
        };
        db.GroupMessages.Add(message);

        foreach (var file in clones)
        {
            db.GroupMessageAttachments.Add(new GroupMessageAttachment
            {
                MessageId = message.Id,
                FileId = file.Id,
                CreatedAt = now
            });
        }

        chat.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (chat, message);
    }

    public async Task<(GroupChat chat, GroupMessage message)> ForwardMessageAsync(
        Guid senderId,
        Guid groupId,
        DirectMessage source,
        bool includeAttachments,
        CancellationToken ct = default)
    {
        if (source.DeletedAt.HasValue)
            throw new InvalidOperationException("Deleted message cannot be forwarded.");

        var chat = await GetChatForMemberAsync(groupId, senderId, ct)
            ?? throw new InvalidOperationException("Group chat not found or access denied.");
        var now = DateTime.UtcNow;

        List<ChatFile> sourceFiles = [];
        if (includeAttachments)
        {
            sourceFiles = await (
                from a in db.DirectMessageAttachments
                join f in db.ChatFiles on a.FileId equals f.Id
                where a.MessageId == source.Id
                orderby a.CreatedAt, a.Id
                select f
            ).ToListAsync(ct);
        }

        var hasSystemMarker = source.Kind == DirectMessageKind.System && !string.IsNullOrWhiteSpace(source.SystemKey);
        if (string.IsNullOrWhiteSpace(source.Text) && sourceFiles.Count == 0 && !hasSystemMarker)
            throw new InvalidOperationException("Message must contain text or attachments.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var clones = sourceFiles.Select(f => new ChatFile
        {
            UploaderId = senderId,
            OriginalFileName = f.OriginalFileName,
            SafeFileName = string.IsNullOrWhiteSpace(f.SafeFileName) ? f.OriginalFileName : f.SafeFileName,
            ContentType = f.ContentType,
            DetectedContentType = string.IsNullOrWhiteSpace(f.DetectedContentType) ? f.ContentType : f.DetectedContentType,
            SizeBytes = f.SizeBytes,
            StoredPath = f.StoredPath,
            Bucket = string.IsNullOrWhiteSpace(f.Bucket) ? "jaezoo-files" : f.Bucket,
            ObjectKey = string.IsNullOrWhiteSpace(f.ObjectKey) ? f.StoredPath : f.ObjectKey,
            Sha256 = f.Sha256 ?? string.Empty,
            Kind = f.Kind,
            ScanStatus = f.ScanStatus,
            IsPotentiallyDangerous = f.IsPotentiallyDangerous,
            RiskNote = f.RiskNote,
            CreatedAt = now,
            IsAttached = true,
            AttachedAt = now
        }).ToList();

        if (clones.Count > 0)
            db.ChatFiles.AddRange(clones);

        var message = new GroupMessage
        {
            GroupChatId = groupId,
            SenderId = senderId,
            Text = (source.Text ?? string.Empty).Trim(),
            SentAt = now,
            GroupSecurityEpoch = Math.Max(1, chat.SecurityEpoch),
            Kind = source.Kind,
            SystemKey = string.IsNullOrWhiteSpace(source.SystemKey) ? null : source.SystemKey.Trim(),
            ForwardedFromMessageId = source.Id
        };
        db.GroupMessages.Add(message);

        foreach (var file in clones)
        {
            db.GroupMessageAttachments.Add(new GroupMessageAttachment
            {
                MessageId = message.Id,
                FileId = file.Id,
                CreatedAt = now
            });
        }

        chat.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (chat, message);
    }

    public async Task<GroupMessage?> ValidateReadCursorAsync(Guid groupId, Guid me, Guid messageId, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(groupId, me, ct))
            return null;

        return await db.GroupMessages
            .AsNoTracking()
            .Where(m => m.GroupChatId == groupId && m.Id == messageId)
            .Select(m => new GroupMessage
            {
                Id = m.Id,
                GroupChatId = m.GroupChatId,
                SenderId = m.SenderId,
                SentAt = m.SentAt
            })
            .FirstOrDefaultAsync(ct);
    }
}
