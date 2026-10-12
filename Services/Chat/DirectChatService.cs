using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Storage;
using JaeZoo.Server.Models.Mls;
using JaeZoo.Server.Services.Mls;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Chat;

public sealed class DirectChatService(AppDbContext db, IObjectStorage storage, MlsDeliveryService mls)
{
    public AppDbContext Db => db;
    public IObjectStorage Storage => storage;

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


    public AttachmentDto ToAttachmentDto(ChatFile f)
    {
        var status = f.ScanStatus.ToString();
        var isClean = f.ScanStatus == JaeZoo.Server.Models.Files.FileScanStatus.Clean;
        var isBlocked = f.ScanStatus == JaeZoo.Server.Models.Files.FileScanStatus.Blocked || f.BlockedAt.HasValue || f.DeletedAt.HasValue;
        var isClientEncrypted = f.ScanStatus == JaeZoo.Server.Models.Files.FileScanStatus.ClientEncrypted;
        var warning = isClean || isClientEncrypted
            ? null
            : isBlocked
                ? "Файл заблокирован проверкой безопасности."
                : "Файл ещё не проверен антивирусом. Скачивание выполняется под вашу ответственность.";

        return new AttachmentDto(
            f.Id,
            f.OriginalFileName,
            f.ContentType,
            f.SizeBytes,
            $"/api/files/{f.Id}/raw",
            IsImage(f.ContentType),
            IsVideo(f.ContentType),
            status,
            isClean || isBlocked,
            isClean,
            warning
        );
    }

    public async Task<Dictionary<Guid, List<AttachmentDto>>> LoadAttachmentsForMessagesAsync(List<Guid> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0)
            return new();

        var rows = await (
            from a in db.DirectMessageAttachments.AsNoTracking()
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


    public MessageDto ToMessageDto(DirectMessage message, IReadOnlyList<AttachmentDto>? attachments = null, Guid? mlsGroupId = null)
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

    public async Task<List<MessageDto>> BuildMessageDtosAsync(IReadOnlyList<DirectMessage> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0)
            return new();

        var ids = messages.Select(m => m.Id).ToList();
        var attachments = await LoadAttachmentsForMessagesAsync(ids, ct);
        var dialogIds = messages.Select(m => m.DialogId).Distinct().ToList();
        var groups = await db.MlsGroups.AsNoTracking()
            .Where(g => g.DialogId != null && dialogIds.Contains(g.DialogId.Value))
            .ToDictionaryAsync(g => g.DialogId!.Value, g => g.Id, ct);

        return messages
            .Select(m => ToMessageDto(
                m,
                attachments.TryGetValue(m.Id, out var att) ? att : null,
                groups.TryGetValue(m.DialogId, out var gid) ? gid : null))
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


    private async Task<List<ChatFile>> LoadAttachableFilesAsync(Guid senderId, IReadOnlyCollection<Guid>? fileIds, bool requireClientEncrypted, CancellationToken ct)
    {
        var ids = (fileIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().Take(10).ToList();
        if (ids.Count == 0) return new List<ChatFile>();

        var files = await db.ChatFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        if (files.Count != ids.Count)
            throw new InvalidOperationException("One or more files were not found.");

        foreach (var f in files)
        {
            if (f.UploaderId != senderId)
                throw new InvalidOperationException("Cannot attach someone else's file.");
            if (f.IsAttached)
                throw new InvalidOperationException("One or more files are already attached.");
            if (requireClientEncrypted && f.ScanStatus != JaeZoo.Server.Models.Files.FileScanStatus.ClientEncrypted)
                throw new InvalidOperationException("Вложения личных сообщений должны быть зашифрованы на устройстве.");
        }
        return files;
    }

    private void AttachFiles(Guid messageId, IEnumerable<ChatFile> files, DateTime now)
    {
        foreach (var f in files)
        {
            db.DirectMessageAttachments.Add(new DirectMessageAttachment { MessageId = messageId, FileId = f.Id, CreatedAt = now });
            f.IsAttached = true;
            f.AttachedAt = now;
        }
    }

    /// <summary>User message: only an MLS ciphertext of the dialog's group is accepted (the server cannot read it).</summary>
    public async Task<(DirectDialog dialog, DirectMessage message, MlsGroupEvent ev)> CreateUserMessageAsync(
        Guid senderId,
        Guid peerId,
        SendMessageRequest request,
        CancellationToken ct = default)
    {
        if (SystemChatService.IsSystemUser(peerId))
            throw new InvalidOperationException("Системный чат JaeZoo только для чтения.");

        var dlg = await GetOrCreateDialogAsync(senderId, peerId, ct);
        var group = await mls.RequireMessageContextAsync(senderId, dlg.Id, null, request.DeviceId, request.Epoch, request.Ciphertext, ct);
        var files = await LoadAttachableFilesAsync(senderId, request.FileIds, requireClientEncrypted: true, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var message = new DirectMessage
        {
            DialogId = dlg.Id,
            SenderId = senderId,
            Ciphertext = request.Ciphertext,
            MlsEpoch = request.Epoch,
            SenderDeviceId = request.DeviceId,
            SentAt = now,
            Kind = DirectMessageKind.User,
        };
        db.DirectMessages.Add(message);
        AttachFiles(message.Id, files, now);
        var ev = mls.AppendMessageEvent(group.Id, MlsEventKind.Message, message.Id, request.Epoch, senderId, request.DeviceId);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (dlg, message, ev);
    }

    /// <summary>Service notice in plain text (system chat JaeZoo, call log entries). Never used for user content.</summary>
    public async Task<(DirectDialog dialog, DirectMessage message)> CreateSystemMessageAsync(
        Guid senderId,
        Guid peerId,
        string? text,
        string systemKey,
        IReadOnlyCollection<Guid>? fileIds = null,
        CancellationToken ct = default)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(systemKey))
            throw new InvalidOperationException("SystemKey is required.");

        var dlg = await GetOrCreateDialogAsync(senderId, peerId, ct);
        var files = await LoadAttachableFilesAsync(senderId, fileIds, requireClientEncrypted: false, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var message = new DirectMessage
        {
            DialogId = dlg.Id,
            SenderId = senderId,
            Text = text,
            SentAt = now,
            Kind = DirectMessageKind.System,
            SystemKey = systemKey.Trim(),
        };
        db.DirectMessages.Add(message);
        AttachFiles(message.Id, files, now);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (dlg, message);
    }

    /// <summary>Edit = new MLS ciphertext for the same message id (re-delivered through the event log).</summary>
    public async Task<(DirectDialog dialog, DirectMessage message)> EditUserMessageAsync(Guid senderId, Guid messageId, EditMessageRequest request, CancellationToken ct = default)
    {
        var message = await db.DirectMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct)
                      ?? throw new KeyNotFoundException("Message not found.");
        if (message.SenderId != senderId) throw new UnauthorizedAccessException("Only the author can edit the message.");
        if (message.Kind != DirectMessageKind.User) throw new InvalidOperationException("System messages cannot be edited.");
        if (message.DeletedAt.HasValue) throw new InvalidOperationException("Deleted message cannot be edited.");

        var dlg = await db.DirectDialogs.FirstAsync(d => d.Id == message.DialogId, ct);
        var group = await mls.RequireMessageContextAsync(senderId, dlg.Id, null, request.DeviceId, request.Epoch, request.Ciphertext, ct);

        message.Ciphertext = request.Ciphertext;
        message.MlsEpoch = request.Epoch;
        message.SenderDeviceId = request.DeviceId;
        message.EditedAt = DateTime.UtcNow;
        mls.AppendMessageEvent(group.Id, MlsEventKind.MessageEdited, message.Id, request.Epoch, senderId, request.DeviceId);
        await db.SaveChangesAsync(ct);
        return (dlg, message);
    }

    /// <summary>Records a deletion in the MLS event log so devices that sync later drop the message too.</summary>
    public async Task AppendDeletionEventAsync(DirectMessage message, CancellationToken ct = default)
    {
        var group = await mls.FindGroupAsync(message.DialogId, null, ct);
        if (group is null || message.Kind != DirectMessageKind.User) return;
        mls.AppendMessageEvent(group.Id, MlsEventKind.MessageDeleted, message.Id, group.Epoch, message.DeletedById ?? message.SenderId, null);
        await db.SaveChangesAsync(ct);
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
