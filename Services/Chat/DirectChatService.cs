using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Chat;

public sealed class DirectChatService(AppDbContext db, IObjectStorage storage)
{
    public AppDbContext Db => db;

    public static (Guid a, Guid b) OrderPair(Guid x, Guid y) => x < y ? (x, y) : (y, x);

    public async Task<bool> AreFriends(Guid me, Guid other, CancellationToken ct = default) =>
        await db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == me && f.AddresseeId == other) ||
             (f.RequesterId == other && f.AddresseeId == me)), ct);

    public async Task<DirectDialog> GetOrCreateDialogAsync(Guid aId, Guid bId, CancellationToken ct = default)
    {
        var (u1, u2) = OrderPair(aId, bId);
        var dlg = await db.DirectDialogs.FirstOrDefaultAsync(d => d.User1Id == u1 && d.User2Id == u2, ct);
        if (dlg is not null) return dlg;

        dlg = new DirectDialog
        {
            User1Id = u1,
            User2Id = u2,
            LastReadAtUser1 = DateTime.MinValue,
            LastReadMessageIdUser1 = Guid.Empty,
            LastReadAtUser2 = DateTime.MinValue,
            LastReadMessageIdUser2 = Guid.Empty
        };
        db.DirectDialogs.Add(dlg);

        try
        {
            await db.SaveChangesAsync(ct);
            return dlg;
        }
        catch (DbUpdateException)
        {
            var existing = await db.DirectDialogs.FirstOrDefaultAsync(d => d.User1Id == u1 && d.User2Id == u2, ct);
            if (existing is not null) return existing;
            throw;
        }
    }

    public static (DateTime at, Guid id) GetReadCursor(DirectDialog dlg, Guid me)
    {
        if (me == dlg.User1Id) return (dlg.LastReadAtUser1, dlg.LastReadMessageIdUser1);
        if (me == dlg.User2Id) return (dlg.LastReadAtUser2, dlg.LastReadMessageIdUser2);
        return (DateTime.MinValue, Guid.Empty);
    }

    public static void SetReadCursor(DirectDialog dlg, Guid me, DateTime atUtc, Guid msgId)
    {
        if (me == dlg.User1Id)
        {
            dlg.LastReadAtUser1 = atUtc;
            dlg.LastReadMessageIdUser1 = msgId;
        }
        else
        {
            dlg.LastReadAtUser2 = atUtc;
            dlg.LastReadMessageIdUser2 = msgId;
        }
    }

    public static DateTime EnsureUtc(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Utc) return dt;
        if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    public static bool IsImage(string? ct) =>
        !string.IsNullOrWhiteSpace(ct) && ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public static bool IsVideo(string? ct) =>
        !string.IsNullOrWhiteSpace(ct) && ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    public AttachmentDto ToAttachmentDto(ChatFile f) => new(
        f.Id,
        f.OriginalFileName,
        f.ContentType,
        f.SizeBytes,
        storage.GetPublicUrl(f.StoredPath),
        IsImage(f.ContentType),
        IsVideo(f.ContentType)
    );

    public async Task<Dictionary<Guid, List<AttachmentDto>>> LoadAttachmentsForMessagesAsync(List<Guid> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0)
            return new();

        var rows = await (
            from a in db.DirectMessageAttachments.AsNoTracking()
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

    public async Task<Dictionary<Guid, MessageForwardInfoDto>> LoadForwardInfosAsync(List<DirectMessage> messages, CancellationToken ct)
    {
        var sourceIds = messages
            .Where(m => m.ForwardedFromMessageId.HasValue)
            .Select(m => m.ForwardedFromMessageId!.Value)
            .Distinct()
            .ToList();

        if (sourceIds.Count == 0)
            return new();

        var result = new Dictionary<Guid, MessageForwardInfoDto>();

        var directSources = await db.DirectMessages
            .AsNoTracking()
            .Where(m => sourceIds.Contains(m.Id))
            .Select(m => new
            {
                m.Id,
                m.SenderId,
                m.Text,
                m.SentAt,
                m.Kind,
                m.SystemKey,
                m.DeletedAt
            })
            .ToListAsync(ct);

        var directAttachmentMessageIds = await db.DirectMessageAttachments
            .AsNoTracking()
            .Where(a => sourceIds.Contains(a.MessageId))
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

        var unresolvedIds = sourceIds.Where(id => !result.ContainsKey(id)).ToList();
        if (unresolvedIds.Count > 0)
        {
            var groupSources = await db.GroupMessages
                .AsNoTracking()
                .Where(m => unresolvedIds.Contains(m.Id))
                .Select(m => new
                {
                    m.Id,
                    m.SenderId,
                    m.Text,
                    m.SentAt,
                    m.Kind,
                    m.SystemKey,
                    m.DeletedAt
                })
                .ToListAsync(ct);

            var groupAttachmentMessageIds = await db.GroupMessageAttachments
                .AsNoTracking()
                .Where(a => unresolvedIds.Contains(a.MessageId))
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
        }

        return result;
    }

    public MessageDto ToMessageDto(
        DirectMessage message,
        IReadOnlyList<AttachmentDto>? attachments = null,
        MessageForwardInfoDto? forwardedFrom = null)
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
            forwardedFrom
        );
    }

    public async Task<List<MessageDto>> BuildMessageDtosAsync(IReadOnlyList<DirectMessage> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0)
            return new();

        var ids = messages.Select(m => m.Id).ToList();
        var attachments = await LoadAttachmentsForMessagesAsync(ids, ct);
        var forwards = await LoadForwardInfosAsync(messages.ToList(), ct);

        return messages
            .Select(m => ToMessageDto(
                m,
                attachments.TryGetValue(m.Id, out var att) ? att : null,
                m.ForwardedFromMessageId.HasValue && forwards.TryGetValue(m.ForwardedFromMessageId.Value, out var fwd) ? fwd : null))
            .ToList();
    }

    public async Task<MessageDto?> GetMessageDtoAsync(Guid dialogId, Guid messageId, CancellationToken ct = default)
    {
        var message = await db.DirectMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.DialogId == dialogId && m.Id == messageId, ct);

        if (message is null)
            return null;

        var dtos = await BuildMessageDtosAsync(new[] { message }, ct);
        return dtos[0];
    }

    public async Task<(int count, Guid? firstId, DateTime? firstAt)> GetUnreadForUserAsync(DirectDialog dlg, Guid userId, CancellationToken ct)
    {
        var (at, id) = GetReadCursor(dlg, userId);

        var q = db.DirectMessages
            .AsNoTracking()
            .Where(m => m.DialogId == dlg.Id && m.SenderId != userId && m.DeletedAt == null)
            .Where(m => m.SentAt > at || (m.SentAt == at && m.Id.CompareTo(id) > 0));

        var count = await q.CountAsync(ct);
        if (count == 0) return (0, null, null);

        var first = await q
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id)
            .Select(m => new { m.Id, m.SentAt })
            .FirstOrDefaultAsync(ct);

        return (count, first?.Id, first?.SentAt);
    }

    public async Task<(DirectDialog dialog, DirectMessage message, List<ChatFile> files)> CreateMessageAsync(
        Guid senderId,
        Guid peerId,
        string? text,
        IReadOnlyCollection<Guid>? fileIds,
        DirectMessageKind kind,
        string? systemKey,
        Guid? forwardedFromMessageId,
        CancellationToken ct = default)
    {
        text = (text ?? string.Empty).Trim();
        var ids = (fileIds ?? Array.Empty<Guid>())
            .Where(x => x != Guid.Empty)
            .Distinct()
            .Take(10)
            .ToList();

        var hasSystemMarker = kind == DirectMessageKind.System && !string.IsNullOrWhiteSpace(systemKey);
        if (string.IsNullOrWhiteSpace(text) && ids.Count == 0 && !hasSystemMarker)
            throw new InvalidOperationException("Message must contain text or attachments.");

        var dlg = await GetOrCreateDialogAsync(senderId, peerId, ct);

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
        var message = new DirectMessage
        {
            DialogId = dlg.Id,
            SenderId = senderId,
            Text = text,
            SentAt = now,
            Kind = kind,
            SystemKey = string.IsNullOrWhiteSpace(systemKey) ? null : systemKey.Trim(),
            ForwardedFromMessageId = forwardedFromMessageId
        };

        db.DirectMessages.Add(message);

        foreach (var f in files)
        {
            db.DirectMessageAttachments.Add(new DirectMessageAttachment
            {
                MessageId = message.Id,
                FileId = f.Id,
                CreatedAt = now
            });

            f.IsAttached = true;
            f.AttachedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (dlg, message, files);
    }

    public async Task<(DirectDialog dialog, DirectMessage message)> ForwardMessageAsync(
        Guid senderId,
        Guid peerId,
        DirectMessage source,
        bool includeAttachments,
        CancellationToken ct = default)
    {
        if (source.DeletedAt.HasValue)
            throw new InvalidOperationException("Deleted message cannot be forwarded.");

        var dlg = await GetOrCreateDialogAsync(senderId, peerId, ct);
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
            ContentType = f.ContentType,
            SizeBytes = f.SizeBytes,
            StoredPath = f.StoredPath,
            CreatedAt = now,
            IsAttached = true,
            AttachedAt = now
        }).ToList();

        if (clones.Count > 0)
            db.ChatFiles.AddRange(clones);

        var message = new DirectMessage
        {
            DialogId = dlg.Id,
            SenderId = senderId,
            Text = (source.Text ?? string.Empty).Trim(),
            SentAt = now,
            Kind = source.Kind,
            SystemKey = string.IsNullOrWhiteSpace(source.SystemKey) ? null : source.SystemKey.Trim(),
            ForwardedFromMessageId = source.Id
        };
        db.DirectMessages.Add(message);

        foreach (var file in clones)
        {
            db.DirectMessageAttachments.Add(new DirectMessageAttachment
            {
                MessageId = message.Id,
                FileId = file.Id,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (dlg, message);
    }

    public async Task<(DirectDialog dialog, DirectMessage message)> ForwardMessageAsync(
        Guid senderId,
        Guid peerId,
        GroupMessage source,
        bool includeAttachments,
        CancellationToken ct = default)
    {
        if (source.DeletedAt.HasValue)
            throw new InvalidOperationException("Deleted message cannot be forwarded.");

        var dlg = await GetOrCreateDialogAsync(senderId, peerId, ct);
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

        var message = new DirectMessage
        {
            DialogId = dlg.Id,
            SenderId = senderId,
            Text = (source.Text ?? string.Empty).Trim(),
            SentAt = now,
            Kind = source.Kind,
            SystemKey = string.IsNullOrWhiteSpace(source.SystemKey) ? null : source.SystemKey.Trim(),
            ForwardedFromMessageId = source.Id
        };
        db.DirectMessages.Add(message);

        foreach (var file in clones)
        {
            db.DirectMessageAttachments.Add(new DirectMessageAttachment
            {
                MessageId = message.Id,
                FileId = file.Id,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (dlg, message);
    }

    public async Task<DirectMessage?> ValidateReadCursorAsync(Guid me, Guid friendId, Guid messageId, CancellationToken ct = default)
    {
        var dlg = await GetOrCreateDialogAsync(me, friendId, ct);
        return await db.DirectMessages
            .AsNoTracking()
            .Where(m => m.DialogId == dlg.Id && m.Id == messageId)
            .Select(m => new DirectMessage
            {
                Id = m.Id,
                DialogId = m.DialogId,
                SenderId = m.SenderId,
                SentAt = m.SentAt
            })
            .FirstOrDefaultAsync(ct);
    }
}
