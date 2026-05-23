using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Files;
using JaeZoo.Server.Services.Chat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Files;

public sealed class FileModerationService(
    AppDbContext db,
    DirectChatService directChat,
    GroupChatService groupChat,
    FileCleanupService cleanup,
    IHubContext<ChatHub> hub,
    ILogger<FileModerationService> log)
{
    public async Task MarkCleanAndBroadcastAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.ChatFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null || file.DeletedAt != null || file.BlockedAt != null) return;

        file.ScanStatus = FileScanStatus.Clean;
        file.RiskNote = null;
        await db.SaveChangesAsync(ct);

        await BroadcastFileMessagesUpdatedAsync(fileId, ct);
    }

    public async Task RemoveDangerousFileAsync(Guid fileId, string reason, CancellationToken ct)
    {
        var file = await db.ChatFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return;
        if (file.BlockedAt != null && file.DeletedAt != null) return;

        var bucket = string.IsNullOrWhiteSpace(file.Bucket) ? "jaezoo-files" : file.Bucket;
        var key = string.IsNullOrWhiteSpace(file.ObjectKey) ? file.StoredPath : file.ObjectKey;

        var affectedFiles = string.IsNullOrWhiteSpace(key)
            ? new List<ChatFile> { file }
            : await db.ChatFiles
                .Where(f => f.DeletedAt == null && f.BlockedAt == null)
                .Where(f => f.Id == file.Id || (f.Bucket == bucket && (f.ObjectKey == key || f.StoredPath == key)))
                .ToListAsync(ct);

        var affectedFileIds = affectedFiles.Select(f => f.Id).Distinct().ToList();

        var now = DateTime.UtcNow;
        var uploaderName = await db.Users.AsNoTracking()
            .Where(u => u.Id == file.UploaderId)
            .Select(u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "пользователь";

        var replacementText = $"Сообщение удалено. Пользователь {uploaderName} загрузил опасный файл.";

        var directRows = await (
            from a in db.DirectMessageAttachments
            join m in db.DirectMessages on a.MessageId equals m.Id
            join d in db.DirectDialogs on m.DialogId equals d.Id
            where affectedFileIds.Contains(a.FileId)
            select new { Attachment = a, Message = m, Dialog = d }
        ).ToListAsync(ct);

        var groupRows = await (
            from a in db.GroupMessageAttachments
            join m in db.GroupMessages on a.MessageId equals m.Id
            where affectedFileIds.Contains(a.FileId)
            select new { Attachment = a, Message = m }
        ).ToListAsync(ct);

        foreach (var row in directRows)
        {
            row.Message.Text = replacementText;
            row.Message.Kind = DirectMessageKind.System;
            row.Message.SystemKey = "dangerous_file_removed";
            row.Message.EditedAt = now;
            row.Message.DeletedAt = null;
            row.Message.DeletedById = null;
        }

        foreach (var row in groupRows)
        {
            row.Message.Text = replacementText;
            row.Message.Kind = DirectMessageKind.System;
            row.Message.SystemKey = "dangerous_file_removed";
            row.Message.EditedAt = now;
            row.Message.DeletedAt = null;
            row.Message.DeletedById = null;
        }

        db.DirectMessageAttachments.RemoveRange(directRows.Select(x => x.Attachment));
        db.GroupMessageAttachments.RemoveRange(groupRows.Select(x => x.Attachment));

        foreach (var affected in affectedFiles)
        {
            affected.ScanStatus = FileScanStatus.Blocked;
            affected.IsPotentiallyDangerous = true;
            affected.RiskNote = string.IsNullOrWhiteSpace(reason) ? "Blocked by antivirus scanner." : reason;
            affected.BlockedAt = now;
            affected.DeletedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await cleanup.DeleteObjectIfNoActiveReferencesAsync(file, ct);

        foreach (var row in directRows)
        {
            var dto = await directChat.GetMessageDtoAsync(row.Message.DialogId, row.Message.Id, ct);
            if (dto is null) continue;

            var peerId = row.Dialog.User1Id == row.Message.SenderId ? row.Dialog.User2Id : row.Dialog.User1Id;
            await hub.Clients.User(row.Message.SenderId.ToString()).SendAsync("ChatMessageUpdated", new ChatMessageUpdatedDto(peerId, dto), ct);
            await hub.Clients.User(peerId.ToString()).SendAsync("ChatMessageUpdated", new ChatMessageUpdatedDto(row.Message.SenderId, dto), ct);
        }

        foreach (var row in groupRows)
        {
            var dto = await groupChat.GetMessageDtoAsync(row.Message.GroupChatId, row.Message.Id, ct);
            if (dto is null) continue;
            var memberIds = await db.GroupChatMembers.AsNoTracking()
                .Where(m => m.GroupChatId == row.Message.GroupChatId)
                .Select(m => m.UserId)
                .ToListAsync(ct);
            foreach (var memberId in memberIds)
                await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatMessageUpdated", new GroupChatMessageUpdatedDto(row.Message.GroupChatId, dto), ct);
        }

        log.LogWarning("Dangerous file removed. FileId={FileId} AffectedFiles={AffectedFiles} Sha256={Sha256} Reason={Reason}", file.Id, affectedFileIds.Count, file.Sha256, reason);
    }

    public async Task BroadcastFileMessagesUpdatedAsync(Guid fileId, CancellationToken ct)
    {
        var directRows = await (
            from a in db.DirectMessageAttachments.AsNoTracking()
            join m in db.DirectMessages.AsNoTracking() on a.MessageId equals m.Id
            join d in db.DirectDialogs.AsNoTracking() on m.DialogId equals d.Id
            where a.FileId == fileId && m.DeletedAt == null
            select new { Message = m, Dialog = d }
        ).ToListAsync(ct);

        foreach (var row in directRows)
        {
            var dto = await directChat.GetMessageDtoAsync(row.Message.DialogId, row.Message.Id, ct);
            if (dto is null) continue;
            var peerId = row.Dialog.User1Id == row.Message.SenderId ? row.Dialog.User2Id : row.Dialog.User1Id;
            await hub.Clients.User(row.Message.SenderId.ToString()).SendAsync("ChatMessageUpdated", new ChatMessageUpdatedDto(peerId, dto), ct);
            await hub.Clients.User(peerId.ToString()).SendAsync("ChatMessageUpdated", new ChatMessageUpdatedDto(row.Message.SenderId, dto), ct);
        }

        var groupRows = await (
            from a in db.GroupMessageAttachments.AsNoTracking()
            join m in db.GroupMessages.AsNoTracking() on a.MessageId equals m.Id
            where a.FileId == fileId && m.DeletedAt == null
            select m
        ).ToListAsync(ct);

        foreach (var message in groupRows)
        {
            var dto = await groupChat.GetMessageDtoAsync(message.GroupChatId, message.Id, ct);
            if (dto is null) continue;
            var memberIds = await db.GroupChatMembers.AsNoTracking()
                .Where(m => m.GroupChatId == message.GroupChatId)
                .Select(m => m.UserId)
                .ToListAsync(ct);
            foreach (var memberId in memberIds)
                await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatMessageUpdated", new GroupChatMessageUpdatedDto(message.GroupChatId, dto), ct);
        }
    }
}
