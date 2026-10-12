using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Mls;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Mls;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Chat;

public sealed class GroupChatService(AppDbContext db, DirectChatService directChat, MlsDeliveryService mls)
{
    public const int MaxGroupMembers = 50;

    public const string SystemUserAddedKey = "group_user_added";
    public const string SystemGroupCallStartedKey = "group_call_started";
    public const string SystemGroupCallEndedKey = "group_call_ended";


    public static string GetAvatarUrl(GroupChat chat) =>
        string.IsNullOrWhiteSpace(chat.AvatarUrl) ? $"/avatars/groups/{chat.Id}" : chat.AvatarUrl;

    private static string? NormalizeDescription(string? description)
    {
        var normalized = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (normalized is not null && normalized.Length > 1000)
            throw new InvalidOperationException("Description is too long.");
        return normalized;
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

    /// <summary>Service notice in plain text (member added, group call started/ended). Never user content.</summary>
    public async Task<(GroupChat chat, GroupMessage message)> CreateSystemMessageAsync(
        Guid senderId,
        Guid groupId,
        string systemKey,
        string text,
        CancellationToken ct = default)
    {
        var chat = await GetChatForMemberAsync(groupId, senderId, ct)
            ?? throw new InvalidOperationException("Group chat not found or access denied.");
        if (string.IsNullOrWhiteSpace(systemKey))
            throw new InvalidOperationException("SystemKey is required.");

        var now = DateTime.UtcNow;
        var message = new GroupMessage
        {
            GroupChatId = groupId,
            SenderId = senderId,
            Text = (text ?? string.Empty).Trim(),
            SentAt = now,
            Kind = DirectMessageKind.System,
            SystemKey = systemKey.Trim(),
        };
        db.GroupMessages.Add(message);
        chat.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (chat, message);
    }

    public async Task<GroupChat> CreateChatAsync(Guid ownerId, string? title, string? description, IReadOnlyCollection<Guid>? memberIds, bool isPublic = false, CancellationToken ct = default)
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

    public async Task<GroupChat> UpdateChatAsync(Guid groupId, Guid me, string? title, string? description, bool? isPublic = null, CancellationToken ct = default)
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

        chat.UpdatedAt = now;
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
        chat.UpdatedAt = DateTime.UtcNow;
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
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(int count, Guid? firstId, DateTime? firstAt)> GetUnreadForUserAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var access = await (
            from member in db.GroupChatMembers.AsNoTracking()
            join groupChat in db.GroupChats.AsNoTracking() on member.GroupChatId equals groupChat.Id
            where member.GroupChatId == groupId && member.UserId == userId
            select new { Member = member, Group = groupChat }
        ).FirstOrDefaultAsync(ct);

        if (access is null)
            return (0, null, null);

        var at = DirectChatService.EnsureUtc(access.Member.LastReadAt);
        var id = access.Member.LastReadMessageId;

        var q = db.GroupMessages
            .AsNoTracking()
            .Where(m => m.GroupChatId == groupId && m.SenderId != userId && m.DeletedAt == null);

        {
            var joinedAt = DirectChatService.EnsureUtc(access.Member.JoinedAt);
            q = q.Where(m => m.SentAt >= joinedAt);
        }

        q = q.Where(m => m.SentAt > at || (m.SentAt == at && m.Id.CompareTo(id) > 0));

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
        var lastMessageQuery = db.GroupMessages
            .AsNoTracking()
            .Where(m => m.GroupChatId == groupId);

        {
            var joinedAt = DirectChatService.EnsureUtc(member.JoinedAt);
            lastMessageQuery = lastMessageQuery.Where(m => m.SentAt >= joinedAt);
        }

        var lastMessage = await lastMessageQuery
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
            chat.IsPublic,
            true);
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
        chat.UpdatedAt = now;
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

    public MessageDto ToMessageDto(GroupMessage message, IReadOnlyList<AttachmentDto>? attachments = null, Guid? mlsGroupId = null)
    {
        var deleted = message.DeletedAt.HasValue;
        return new MessageDto(
            message.Id,
            message.SenderId,
            deleted ? string.Empty : message.Text,
            message.SentAt,
            deleted ? Array.Empty<AttachmentDto>() : attachments,
            message.Kind,
            message.SystemKey,
            message.EditedAt,
            message.DeletedAt,
            message.DeletedById,
            deleted ? null : message.Ciphertext,
            message.MlsEpoch,
            message.SenderDeviceId,
            mlsGroupId);
    }

    public async Task<List<MessageDto>> BuildMessageDtosAsync(IReadOnlyList<GroupMessage> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0)
            return new();

        var ids = messages.Select(m => m.Id).ToList();
        var attachments = await LoadAttachmentsForMessagesAsync(ids, ct);
        var chatIds = messages.Select(m => m.GroupChatId).Distinct().ToList();
        var groups = await db.MlsGroups.AsNoTracking()
            .Where(g => g.GroupChatId != null && chatIds.Contains(g.GroupChatId.Value))
            .ToDictionaryAsync(g => g.GroupChatId!.Value, g => g.Id, ct);

        return messages.Select(m => ToMessageDto(
            m,
            attachments.TryGetValue(m.Id, out var att) ? att : null,
            groups.TryGetValue(m.GroupChatId, out var gid) ? gid : null)).ToList();
    }

    public async Task<MessageDto?> GetMessageDtoAsync(Guid groupId, Guid messageId, CancellationToken ct = default)
    {
        var message = await db.GroupMessages.AsNoTracking().FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.Id == messageId, ct);
        if (message is null)
            return null;

        var dtos = await BuildMessageDtosAsync(new[] { message }, ct);
        return dtos[0];
    }

    /// <summary>User message: only an MLS ciphertext of the chat's MLS group is accepted.</summary>
    public async Task<(GroupChat chat, GroupMessage message, MlsGroupEvent ev)> CreateUserMessageAsync(
        Guid senderId,
        Guid groupId,
        SendMessageRequest request,
        CancellationToken ct = default)
    {
        var chat = await GetChatForMemberAsync(groupId, senderId, ct)
            ?? throw new InvalidOperationException("Group chat not found or access denied.");
        var group = await mls.RequireMessageContextAsync(senderId, null, groupId, request.DeviceId, request.Epoch, request.Ciphertext, ct);

        var ids = (request.FileIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().Take(10).ToList();
        var files = ids.Count == 0 ? new List<ChatFile>() : await db.ChatFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        if (files.Count != ids.Count)
            throw new InvalidOperationException("One or more files were not found.");
        foreach (var f in files)
        {
            if (f.UploaderId != senderId)
                throw new InvalidOperationException("Cannot attach someone else's file.");
            if (f.IsAttached)
                throw new InvalidOperationException("One or more files are already attached.");
            if (f.ScanStatus != JaeZoo.Server.Models.Files.FileScanStatus.ClientEncrypted)
                throw new InvalidOperationException("Вложения сообщений группы должны быть зашифрованы на устройстве.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var message = new GroupMessage
        {
            GroupChatId = groupId,
            SenderId = senderId,
            Ciphertext = request.Ciphertext,
            MlsEpoch = request.Epoch,
            SenderDeviceId = request.DeviceId,
            SentAt = now,
            Kind = DirectMessageKind.User,
        };
        db.GroupMessages.Add(message);

        foreach (var f in files)
        {
            db.GroupMessageAttachments.Add(new GroupMessageAttachment { MessageId = message.Id, FileId = f.Id, CreatedAt = now });
            f.IsAttached = true;
            f.AttachedAt = now;
        }

        var ev = mls.AppendMessageEvent(group.Id, MlsEventKind.Message, message.Id, request.Epoch, senderId, request.DeviceId);
        chat.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (chat, message, ev);
    }

    /// <summary>Edit = new MLS ciphertext for the same message id (re-delivered through the event log).</summary>
    public async Task<GroupMessage> EditUserMessageAsync(Guid senderId, Guid messageId, EditMessageRequest request, CancellationToken ct = default)
    {
        var message = await db.GroupMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct)
                      ?? throw new KeyNotFoundException("Message not found.");
        if (message.SenderId != senderId) throw new UnauthorizedAccessException("Only the author can edit the message.");
        if (message.Kind != DirectMessageKind.User) throw new InvalidOperationException("System messages cannot be edited.");
        if (message.DeletedAt.HasValue) throw new InvalidOperationException("Deleted message cannot be edited.");
        if (!await IsMemberAsync(message.GroupChatId, senderId, ct)) throw new UnauthorizedAccessException("Access denied.");

        var group = await mls.RequireMessageContextAsync(senderId, null, message.GroupChatId, request.DeviceId, request.Epoch, request.Ciphertext, ct);
        message.Ciphertext = request.Ciphertext;
        message.MlsEpoch = request.Epoch;
        message.SenderDeviceId = request.DeviceId;
        message.EditedAt = DateTime.UtcNow;
        mls.AppendMessageEvent(group.Id, MlsEventKind.MessageEdited, message.Id, request.Epoch, senderId, request.DeviceId);
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>Records a deletion in the MLS event log so devices that sync later drop the message too.</summary>
    public async Task AppendDeletionEventAsync(GroupMessage message, CancellationToken ct = default)
    {
        var group = await mls.FindGroupAsync(null, message.GroupChatId, ct);
        if (group is null || message.Kind != DirectMessageKind.User) return;
        mls.AppendMessageEvent(group.Id, MlsEventKind.MessageDeleted, message.Id, group.Epoch, message.DeletedById ?? message.SenderId, null);
        await db.SaveChangesAsync(ct);
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
