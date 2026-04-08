using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Chat;

public sealed class GroupChatService(AppDbContext db, DirectChatService directChat)
{
    public const int MaxGroupMembers = 50;

    public async Task<bool> IsMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default) =>
        await db.GroupChatMembers.AsNoTracking().AnyAsync(m => m.GroupChatId == groupId && m.UserId == userId, ct);

    public async Task<GroupChat?> GetChatForMemberAsync(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var isMember = await IsMemberAsync(groupId, userId, ct);
        if (!isMember)
            return null;

        return await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct);
    }

    public async Task<GroupChat> CreateChatAsync(Guid ownerId, string? title, IReadOnlyCollection<Guid>? memberIds, CancellationToken ct = default)
    {
        var normalizedTitle = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            throw new InvalidOperationException("Title is required.");
        if (normalizedTitle.Length > 120)
            throw new InvalidOperationException("Title is too long.");

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
            OwnerId = ownerId,
            MemberLimit = MaxGroupMembers,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.GroupChats.Add(chat);
        db.GroupChatMembers.Add(new GroupChatMember
        {
            GroupChatId = chat.Id,
            UserId = ownerId,
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

    public async Task<GroupChat> UpdateTitleAsync(Guid groupId, Guid me, string? title, CancellationToken ct = default)
    {
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new InvalidOperationException("Group chat not found.");

        if (chat.OwnerId != me)
            throw new InvalidOperationException("Only the owner can rename the group.");

        var normalizedTitle = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            throw new InvalidOperationException("Title is required.");
        if (normalizedTitle.Length > 120)
            throw new InvalidOperationException("Title is too long.");

        chat.Title = normalizedTitle;
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return chat;
    }

    public async Task<IReadOnlyList<GroupChatMember>> AddMembersAsync(Guid groupId, Guid me, IReadOnlyCollection<Guid>? userIds, CancellationToken ct = default)
    {
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new InvalidOperationException("Group chat not found.");

        if (chat.OwnerId != me)
            throw new InvalidOperationException("Only the owner can add members.");

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
            throw new InvalidOperationException("You can add only accepted friends.");

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

        if (chat.OwnerId != me)
            throw new InvalidOperationException("Only the owner can remove members.");
        if (userId == chat.OwnerId)
            throw new InvalidOperationException("Owner cannot be removed from the group.");

        var member = await db.GroupChatMembers.FirstOrDefaultAsync(m => m.GroupChatId == groupId && m.UserId == userId, ct);
        if (member is null)
            throw new InvalidOperationException("Member was not found.");

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
        return await (
            from m in db.GroupChatMembers.AsNoTracking()
            join u in db.Users.AsNoTracking() on m.UserId equals u.Id
            join g in db.GroupChats.AsNoTracking() on m.GroupChatId equals g.Id
            where m.GroupChatId == groupId
            orderby m.JoinedAt, m.Id
            select new GroupChatMemberDto(
                u.Id,
                u.UserName,
                u.Email,
                u.AvatarUrl,
                m.JoinedAt,
                g.OwnerId == u.Id
            )
        ).ToListAsync(ct);
    }

    public async Task<GroupChatSummaryDto?> GetSummaryAsync(Guid groupId, Guid me, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(groupId, me, ct))
            return null;

        var chat = await db.GroupChats.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (chat is null)
            return null;

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
            chat.OwnerId,
            memberCount,
            chat.MemberLimit,
            chat.CreatedAt,
            chat.UpdatedAt,
            lastMessage?.SentAt,
            lastMessageDto,
            unread.count,
            unread.firstId,
            unread.firstAt);
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

    public AttachmentDto ToAttachmentDto(ChatFile f) => directChat.ToAttachmentDto(f);

    public async Task<Dictionary<Guid, List<AttachmentDto>>> LoadAttachmentsForMessagesAsync(List<Guid> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0)
            return new();

        var rows = await (
            from a in db.GroupMessageAttachments.AsNoTracking()
            join f in db.ChatFiles.AsNoTracking() on a.FileId equals f.Id
            where messageIds.Contains(a.MessageId)
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

        var sources = await db.GroupMessages
            .AsNoTracking()
            .Where(m => sourceIds.Contains(m.Id))
            .Select(m => new { m.Id, m.SenderId, m.Text, m.SentAt, m.Kind, m.SystemKey, m.DeletedAt })
            .ToListAsync(ct);

        var attachmentMessageIds = await db.GroupMessageAttachments
            .AsNoTracking()
            .Where(a => sourceIds.Contains(a.MessageId))
            .Select(a => a.MessageId)
            .Distinct()
            .ToListAsync(ct);
        var hasAttachments = attachmentMessageIds.ToHashSet();

        return sources.ToDictionary(
            x => x.Id,
            x => new MessageForwardInfoDto(
                x.Id,
                x.SenderId,
                x.DeletedAt.HasValue ? string.Empty : x.Text,
                x.SentAt,
                hasAttachments.Contains(x.Id) && !x.DeletedAt.HasValue,
                x.Kind,
                x.DeletedAt.HasValue ? null : x.SystemKey,
                x.DeletedAt));
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
            forwardedFrom);
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
            ContentType = f.ContentType,
            SizeBytes = f.SizeBytes,
            StoredPath = f.StoredPath,
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
