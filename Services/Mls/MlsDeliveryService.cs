using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Mls;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Mls;

public sealed class MlsDeliveryException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    public static MlsDeliveryException EpochConflict(long current) =>
        new("epoch_conflict", $"Группа уже в эпохе {current}. Обработайте новые события и повторите.", 409);

    public static MlsDeliveryException Forbidden(string message = "Нет доступа к этому чату.") => new("forbidden", message, 403);
    public static MlsDeliveryException NotFound(string message) => new("not_found", message, 404);
    public static MlsDeliveryException Invalid(string message) => new("invalid", message, 400);
}

/// <summary>
/// MLS Delivery Service (RFC 9420, section 4 / RFC 9750): devices, KeyPackages, commit ordering by epoch,
/// Welcome delivery and an ordered per-group event log. Never sees keys or plaintext.
/// </summary>
public sealed class MlsDeliveryService(AppDbContext db)
{
    /// <summary>Maximum active devices per user (one constant, change here).</summary>
    public const int MaxDevicesPerUser = 5;

    /// <summary>Devices without activity this long are revoked automatically.</summary>
    public static readonly TimeSpan InactiveDeviceLifetime = TimeSpan.FromDays(60);

    /// <summary>One-time KeyPackages a device keeps on the server; clients refill below half of it.</summary>
    public const int KeyPackageTarget = 50;

    public const int MaxKeyPackageBytes = 16 * 1024;
    public const int MaxCommitBytes = 256 * 1024;
    public const int MaxMessageBytes = 128 * 1024;

    /// <summary>Messages may be encrypted in the current epoch or up to this many epochs back (receivers keep them).</summary>
    public const int MessageEpochTolerance = 2;

    public const string DeviceLimitMessage = "Достигнут лимит устройств, отзовите одно из списка.";

    // ---------- devices ----------

    public async Task<UserDevice> RegisterDeviceAsync(Guid userId, Guid? sessionId, RegisterDeviceRequest req, CancellationToken ct)
    {
        if (req.DeviceId == Guid.Empty)
            throw MlsDeliveryException.Invalid("Не указан идентификатор устройства.");
        if (req.SignaturePublicKey is not { Length: > 0 and <= 256 })
            throw MlsDeliveryException.Invalid("Некорректный ключ устройства.");

        var now = DateTime.UtcNow;
        var device = await db.UserDevices.FirstOrDefaultAsync(d => d.Id == req.DeviceId, ct);
        if (device is not null && device.UserId != userId)
            throw MlsDeliveryException.Invalid("Идентификатор устройства занят.");
        if (device?.RevokedAt is not null)
            throw new MlsDeliveryException("device_revoked", "Это устройство отозвано. Войдите заново.", 409);

        if (device is null)
        {
            var active = await db.UserDevices.CountAsync(d => d.UserId == userId && d.RevokedAt == null, ct);
            if (active >= MaxDevicesPerUser)
                throw new MlsDeliveryException("device_limit", DeviceLimitMessage, 409);

            device = new UserDevice { Id = req.DeviceId, UserId = userId, CreatedAt = now };
            db.UserDevices.Add(device);
        }

        // A new login on the same device closes the previous session of that device.
        if (device.SessionId is { } oldSession && sessionId is { } newSession && oldSession != newSession)
        {
            await db.UserSessions
                .Where(s => s.Id == oldSession && s.UserId == userId && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        }

        device.SessionId = sessionId ?? device.SessionId;
        device.Name = Clean(req.Name, 128, "Устройство");
        device.Platform = Clean(req.Platform, 64, string.Empty);
        device.ClientVersion = Clean(req.ClientVersion, 32, string.Empty);
        device.SignaturePublicKey = req.SignaturePublicKey;
        device.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        return device;
    }

    public async Task<List<UserDeviceDto>> ListMyDevicesAsync(Guid userId, Guid? currentSessionId, CancellationToken ct)
    {
        var rows = await db.UserDevices.AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderBy(d => d.RevokedAt != null)
            .ThenByDescending(d => d.LastSeenAt)
            .Take(50)
            .ToListAsync(ct);

        return rows.Select(d => new UserDeviceDto(
            d.Id, d.Name, d.Platform, d.ClientVersion, d.CreatedAt, d.LastSeenAt,
            currentSessionId.HasValue && d.SessionId == currentSessionId && d.RevokedAt == null,
            d.RevokedAt, d.RevokeReason)).ToList();
    }

    public async Task RenameDeviceAsync(Guid userId, Guid deviceId, string? name, CancellationToken ct)
    {
        var device = await db.UserDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId && d.RevokedAt == null, ct)
                     ?? throw MlsDeliveryException.NotFound("Устройство не найдено.");
        device.Name = Clean(name, 128, device.Name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Revokes a device: ends its session, drops its KeyPackages and pending Welcomes.
    /// Removing its leaves is done by the other members' clients (Remove commits) after they get
    /// the returned notification targets. Returns user ids sharing a group with the device.
    /// </summary>
    public async Task<List<Guid>> RevokeDeviceAsync(Guid userId, Guid deviceId, string reason, CancellationToken ct)
    {
        var device = await db.UserDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, ct)
                     ?? throw MlsDeliveryException.NotFound("Устройство не найдено.");
        var now = DateTime.UtcNow;
        if (device.RevokedAt is null)
        {
            device.RevokedAt = now;
            device.RevokeReason = reason;
        }

        if (device.SessionId is { } sid)
        {
            await db.UserSessions.Where(s => s.Id == sid && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        }

        await db.MlsKeyPackages.Where(k => k.DeviceId == deviceId).ExecuteDeleteAsync(ct);
        await db.MlsWelcomes.Where(w => w.TargetDeviceId == deviceId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);

        return await UsersSharingGroupsWithDeviceAsync(deviceId, ct);
    }

    public async Task<List<Guid>> UsersSharingGroupsWithDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        var groupIds = db.MlsGroupMembers.Where(m => m.DeviceId == deviceId).Select(m => m.GroupId);
        return await db.MlsGroupMembers.AsNoTracking()
            .Where(m => groupIds.Contains(m.GroupId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<UserDevice> RequireActiveDeviceAsync(Guid userId, Guid deviceId, CancellationToken ct)
    {
        var device = await db.UserDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId, ct);
        if (device is null || device.RevokedAt is not null)
            throw new MlsDeliveryException("device_inactive", "Устройство не зарегистрировано или отозвано.", 403);
        return device;
    }

    /// <summary>Touches LastSeenAt at most once per hour (cheap on hot paths).</summary>
    public async Task TouchDeviceAsync(UserDevice device, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - device.LastSeenAt < TimeSpan.FromHours(1)) return;
        device.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
    }

    // ---------- key packages ----------

    public async Task<KeyPackageStatusDto> UploadKeyPackagesAsync(Guid userId, UploadKeyPackagesRequest req, CancellationToken ct)
    {
        var device = await RequireActiveDeviceAsync(userId, req.DeviceId, ct);
        var incoming = (req.KeyPackages ?? Array.Empty<byte[]>()).Where(k => k is { Length: > 0 }).ToList();
        if (incoming.Any(k => k.Length > MaxKeyPackageBytes) || req.LastResort is { Length: > MaxKeyPackageBytes })
            throw MlsDeliveryException.Invalid("KeyPackage слишком большой.");

        var existing = await db.MlsKeyPackages.CountAsync(k => k.DeviceId == device.Id && !k.IsLastResort, ct);
        var room = Math.Max(0, KeyPackageTarget * 2 - existing);
        var now = DateTime.UtcNow;
        foreach (var kp in incoming.Take(room))
            db.MlsKeyPackages.Add(new MlsKeyPackage { UserId = userId, DeviceId = device.Id, Data = kp, CreatedAt = now });

        if (req.LastResort is { Length: > 0 } lastResort)
        {
            await db.MlsKeyPackages.Where(k => k.DeviceId == device.Id && k.IsLastResort).ExecuteDeleteAsync(ct);
            db.MlsKeyPackages.Add(new MlsKeyPackage { UserId = userId, DeviceId = device.Id, Data = lastResort, IsLastResort = true, CreatedAt = now });
        }

        device.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        return await KeyPackageStatusAsync(userId, device.Id, ct);
    }

    public async Task<KeyPackageStatusDto> KeyPackageStatusAsync(Guid userId, Guid deviceId, CancellationToken ct)
    {
        await RequireActiveDeviceAsync(userId, deviceId, ct);
        var oneTime = await db.MlsKeyPackages.CountAsync(k => k.DeviceId == deviceId && !k.IsLastResort, ct);
        var lastResort = await db.MlsKeyPackages.AnyAsync(k => k.DeviceId == deviceId && k.IsLastResort, ct);
        return new KeyPackageStatusDto(oneTime, lastResort, KeyPackageTarget);
    }

    /// <summary>
    /// Hands out one KeyPackage per requested device: a one-time package (deleted atomically) or,
    /// when the pool is empty, the device's last-resort package (kept).
    /// </summary>
    public async Task<List<ClaimedKeyPackageDto>> ClaimKeyPackagesAsync(Guid requesterId, IReadOnlyList<Guid> deviceIds, CancellationToken ct)
    {
        var ids = deviceIds.Where(x => x != Guid.Empty).Distinct().Take(64).ToList();
        var devices = await db.UserDevices.AsNoTracking()
            .Where(d => ids.Contains(d.Id) && d.RevokedAt == null)
            .Select(d => new { d.Id, d.UserId })
            .ToListAsync(ct);

        foreach (var ownerId in devices.Select(d => d.UserId).Distinct())
        {
            if (!await SharesConversationAsync(requesterId, ownerId, ct))
                throw MlsDeliveryException.Forbidden("Нельзя получить ключи этого пользователя.");
        }

        var result = new List<ClaimedKeyPackageDto>();
        foreach (var d in devices)
        {
            ClaimedKeyPackageDto? claimed = null;
            for (var attempt = 0; attempt < 5 && claimed is null; attempt++)
            {
                var candidate = await db.MlsKeyPackages.AsNoTracking()
                    .Where(k => k.DeviceId == d.Id && !k.IsLastResort)
                    .OrderBy(k => k.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (candidate is null) break;
                // Atomic "pop": whoever deletes the row owns the package.
                if (await db.MlsKeyPackages.Where(k => k.Id == candidate.Id).ExecuteDeleteAsync(ct) == 1)
                    claimed = new ClaimedKeyPackageDto(d.UserId, d.Id, candidate.Data, false);
            }

            if (claimed is null)
            {
                var lastResort = await db.MlsKeyPackages.AsNoTracking()
                    .Where(k => k.DeviceId == d.Id && k.IsLastResort)
                    .FirstOrDefaultAsync(ct);
                if (lastResort is not null)
                    claimed = new ClaimedKeyPackageDto(d.UserId, d.Id, lastResort.Data, true);
            }

            if (claimed is not null) result.Add(claimed);
        }

        return result;
    }

    private async Task<bool> SharesConversationAsync(Guid a, Guid b, CancellationToken ct)
    {
        if (a == b) return true;
        var (u1, u2) = a < b ? (a, b) : (b, a);
        if (await db.DirectDialogs.AnyAsync(d => d.User1Id == u1 && d.User2Id == u2, ct)) return true;
        if (await db.Friendships.AnyAsync(f => f.Status == FriendshipStatus.Accepted &&
                ((f.RequesterId == a && f.AddresseeId == b) || (f.RequesterId == b && f.AddresseeId == a)), ct)) return true;
        var groupsOfA = db.GroupChatMembers.Where(m => m.UserId == a).Select(m => m.GroupChatId);
        return await db.GroupChatMembers.AnyAsync(m => m.UserId == b && groupsOfA.Contains(m.GroupChatId), ct);
    }

    // ---------- conversations / groups ----------

    /// <summary>Participants of a conversation; throws if <paramref name="me"/> is not one of them.</summary>
    public async Task<(List<Guid> participants, Guid? dialogId, Guid? groupChatId)> ResolveConversationAsync(
        Guid me, MlsConversationKind kind, Guid conversationId, bool createDialog, CancellationToken ct)
    {
        if (kind == MlsConversationKind.Direct)
        {
            var peer = conversationId;
            if (peer == me || peer == Guid.Empty) throw MlsDeliveryException.Invalid("Некорректный собеседник.");
            if (Chat.SystemChatService.IsSystemUser(peer)) throw MlsDeliveryException.Invalid("Системный чат не шифруется.");
            var (u1, u2) = me < peer ? (me, peer) : (peer, me);
            var dialog = await db.DirectDialogs.FirstOrDefaultAsync(d => d.User1Id == u1 && d.User2Id == u2, ct);
            if (dialog is null)
            {
                var friends = await db.Friendships.AnyAsync(f => f.Status == FriendshipStatus.Accepted &&
                    ((f.RequesterId == me && f.AddresseeId == peer) || (f.RequesterId == peer && f.AddresseeId == me)), ct);
                if (!friends) throw MlsDeliveryException.Forbidden("Вы не друзья.");
                if (!createDialog) return (new List<Guid> { me, peer }, null, null);
                dialog = new DirectDialog { User1Id = u1, User2Id = u2 };
                db.DirectDialogs.Add(dialog);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException)
                {
                    db.Entry(dialog).State = EntityState.Detached;
                    dialog = await db.DirectDialogs.FirstAsync(d => d.User1Id == u1 && d.User2Id == u2, ct);
                }
            }
            return (new List<Guid> { me, peer }, dialog.Id, null);
        }

        if (kind == MlsConversationKind.Group)
        {
            var members = await db.GroupChatMembers.AsNoTracking()
                .Where(m => m.GroupChatId == conversationId)
                .Select(m => m.UserId)
                .ToListAsync(ct);
            if (!members.Contains(me)) throw MlsDeliveryException.Forbidden();
            return (members, null, conversationId);
        }

        throw MlsDeliveryException.Invalid("Неизвестный тип чата.");
    }

    public async Task<MlsGroupState?> FindGroupAsync(Guid? dialogId, Guid? groupChatId, CancellationToken ct)
    {
        if (dialogId.HasValue) return await db.MlsGroups.AsNoTracking().FirstOrDefaultAsync(g => g.DialogId == dialogId, ct);
        if (groupChatId.HasValue) return await db.MlsGroups.AsNoTracking().FirstOrDefaultAsync(g => g.GroupChatId == groupChatId, ct);
        return null;
    }

    public async Task<MlsConversationDto> GetConversationAsync(Guid me, MlsConversationKind kind, Guid conversationId, CancellationToken ct)
    {
        var (participants, dialogId, groupChatId) = await ResolveConversationAsync(me, kind, conversationId, createDialog: false, ct);
        var group = await FindGroupAsync(dialogId, groupChatId, ct);
        var expected = await db.UserDevices.AsNoTracking()
            .Where(d => participants.Contains(d.UserId) && d.RevokedAt == null)
            .Select(d => new DeviceRefDto(d.UserId, d.Id))
            .ToListAsync(ct);
        var members = group is null
            ? new List<DeviceRefDto>()
            : await db.MlsGroupMembers.AsNoTracking()
                .Where(m => m.GroupId == group.Id)
                .Select(m => new DeviceRefDto(m.UserId, m.DeviceId))
                .ToListAsync(ct);
        return new MlsConversationDto(kind, conversationId, group?.Id, group?.Epoch ?? 0, members, expected);
    }

    /// <summary>Registers a new MLS group at epoch 0 with the creator's leaf. Conflict if the conversation already has one.</summary>
    public async Task<MlsGroupState> CreateGroupAsync(Guid me, CreateMlsGroupRequest req, CancellationToken ct)
    {
        if (req.GroupId == Guid.Empty) throw MlsDeliveryException.Invalid("Не указан идентификатор группы.");
        await RequireActiveDeviceAsync(me, req.DeviceId, ct);
        var (_, dialogId, groupChatId) = await ResolveConversationAsync(me, req.Kind, req.ConversationId, createDialog: true, ct);

        var existing = await FindGroupAsync(dialogId, groupChatId, ct);
        if (existing is not null)
            throw new MlsDeliveryException("group_exists", "У этого чата уже есть MLS-группа.", 409);

        var now = DateTime.UtcNow;
        var group = new MlsGroupState
        {
            Id = req.GroupId,
            Kind = req.Kind,
            DialogId = dialogId,
            GroupChatId = groupChatId,
            Epoch = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MlsGroups.Add(group);
        db.MlsGroupMembers.Add(new MlsGroupMember { GroupId = group.Id, DeviceId = req.DeviceId, UserId = me, AddedAtEpoch = 0 });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new MlsDeliveryException("group_exists", "У этого чата уже есть MLS-группа.", 409);
        }
        // The epoch is advanced with set-based updates; never keep a tracked copy that would go stale.
        db.Entry(group).State = EntityState.Detached;
        return group;
    }

    public async Task<(MlsGroupState group, List<Guid> participants)> RequireGroupForMemberAsync(Guid me, Guid groupId, CancellationToken ct)
    {
        var group = await db.MlsGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct)
                    ?? throw MlsDeliveryException.NotFound("Группа не найдена.");
        var participants = await ParticipantsAsync(group, ct);
        if (!participants.Contains(me)) throw MlsDeliveryException.Forbidden();
        return (group, participants);
    }

    public async Task<List<Guid>> ParticipantsAsync(MlsGroupState group, CancellationToken ct)
    {
        if (group.DialogId is { } dialogId)
        {
            var d = await db.DirectDialogs.AsNoTracking().FirstAsync(x => x.Id == dialogId, ct);
            return new List<Guid> { d.User1Id, d.User2Id };
        }
        return await db.GroupChatMembers.AsNoTracking()
            .Where(m => m.GroupChatId == group.GroupChatId)
            .Select(m => m.UserId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Accepts a commit only if it is built on the current epoch (first writer wins; the loser gets
    /// <c>epoch_conflict</c> and retries after processing the winner). Records the event, roster changes
    /// and Welcomes in one transaction.
    /// </summary>
    public async Task<(MlsGroupEvent ev, List<MlsWelcome> welcomes, List<Guid> participants)> SubmitCommitAsync(
        Guid me, Guid groupId, SubmitCommitRequest req, CancellationToken ct)
    {
        if (req.Commit is not { Length: > 0 and <= MaxCommitBytes })
            throw MlsDeliveryException.Invalid("Некорректный коммит.");
        if (req.Welcome is { Length: > MaxCommitBytes })
            throw MlsDeliveryException.Invalid("Welcome слишком большой.");

        var (group, participants) = await RequireGroupForMemberAsync(me, groupId, ct);
        await RequireActiveDeviceAsync(me, req.DeviceId, ct);
        if (!await db.MlsGroupMembers.AnyAsync(m => m.GroupId == groupId && m.DeviceId == req.DeviceId, ct))
            throw MlsDeliveryException.Forbidden("Устройство не состоит в группе.");

        var added = (req.Added ?? Array.Empty<DeviceRefDto>()).DistinctBy(x => x.DeviceId).ToList();
        var removed = (req.Removed ?? Array.Empty<DeviceRefDto>()).DistinctBy(x => x.DeviceId).ToList();
        if (added.Count > 0 && req.Welcome is not { Length: > 0 })
            throw MlsDeliveryException.Invalid("Для добавленных устройств нужен Welcome.");

        // Only active devices of conversation participants may be added.
        var addedIds = added.Select(a => a.DeviceId).ToList();
        var validAdded = await db.UserDevices.AsNoTracking()
            .Where(d => addedIds.Contains(d.Id) && d.RevokedAt == null && participants.Contains(d.UserId))
            .Select(d => new { d.Id, d.UserId })
            .ToListAsync(ct);
        if (validAdded.Count != added.Count || added.Any(a => validAdded.All(v => v.Id != a.DeviceId || v.UserId != a.UserId)))
            throw MlsDeliveryException.Invalid("Добавлять можно только активные устройства участников чата.");
        if (removed.Any(r => r.DeviceId == req.DeviceId))
            throw MlsDeliveryException.Invalid("Нельзя удалить своё устройство своим же коммитом.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var advanced = await db.MlsGroups
            .Where(g => g.Id == groupId && g.Epoch == req.Epoch)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Epoch, g => g.Epoch + 1).SetProperty(g => g.UpdatedAt, now), ct);
        if (advanced != 1)
        {
            await tx.RollbackAsync(ct);
            var current = await db.MlsGroups.AsNoTracking().Where(g => g.Id == groupId).Select(g => g.Epoch).FirstAsync(ct);
            throw MlsDeliveryException.EpochConflict(current);
        }

        var ev = new MlsGroupEvent
        {
            GroupId = groupId,
            Kind = MlsEventKind.Commit,
            Epoch = req.Epoch,
            SenderUserId = me,
            SenderDeviceId = req.DeviceId,
            Payload = req.Commit,
            CreatedAt = now,
        };
        db.MlsGroupEvents.Add(ev);

        var removedIds = removed.Select(r => r.DeviceId).ToList();
        if (removedIds.Count > 0)
            await db.MlsGroupMembers.Where(m => m.GroupId == groupId && removedIds.Contains(m.DeviceId)).ExecuteDeleteAsync(ct);

        var existingMembers = await db.MlsGroupMembers.Where(m => m.GroupId == groupId && addedIds.Contains(m.DeviceId))
            .Select(m => m.DeviceId).ToListAsync(ct);
        foreach (var a in validAdded.Where(a => !existingMembers.Contains(a.Id)))
            db.MlsGroupMembers.Add(new MlsGroupMember { GroupId = groupId, DeviceId = a.Id, UserId = a.UserId, AddedAtEpoch = req.Epoch + 1 });

        await db.SaveChangesAsync(ct); // assigns ev.Id

        var welcomes = new List<MlsWelcome>();
        foreach (var a in validAdded)
        {
            var w = new MlsWelcome
            {
                GroupId = groupId,
                TargetUserId = a.UserId,
                TargetDeviceId = a.Id,
                Epoch = req.Epoch + 1,
                CommitEventId = ev.Id,
                Data = req.Welcome!,
                CreatedAt = now,
            };
            db.MlsWelcomes.Add(w);
            welcomes.Add(w);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (ev, welcomes, participants);
    }

    public async Task<List<MlsGroupEvent>> GetEventsAsync(Guid me, Guid groupId, long afterId, int take, CancellationToken ct)
    {
        await RequireGroupForMemberAsync(me, groupId, ct);
        return await db.MlsGroupEvents.AsNoTracking()
            .Where(e => e.GroupId == groupId && e.Id > afterId)
            .OrderBy(e => e.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
    }

    public async Task<List<MlsWelcomeDto>> PendingWelcomesAsync(Guid me, Guid deviceId, CancellationToken ct)
    {
        var device = await RequireActiveDeviceAsync(me, deviceId, ct);
        await TouchDeviceAsync(device, ct);
        var rows = await (
            from w in db.MlsWelcomes.AsNoTracking()
            join g in db.MlsGroups.AsNoTracking() on w.GroupId equals g.Id
            where w.TargetDeviceId == deviceId && w.TargetUserId == me
            orderby w.CreatedAt
            select new { w, g }
        ).Take(100).ToListAsync(ct);

        var result = new List<MlsWelcomeDto>();
        foreach (var r in rows)
        {
            var conversationId = await ConversationIdForViewerAsync(r.g, me, ct);
            result.Add(new MlsWelcomeDto(r.w.Id, r.w.GroupId, r.w.Epoch, r.w.CommitEventId, r.g.Kind, conversationId, r.w.Data));
        }
        return result;
    }

    public async Task AckWelcomeAsync(Guid me, Guid welcomeId, CancellationToken ct) =>
        await db.MlsWelcomes.Where(w => w.Id == welcomeId && w.TargetUserId == me).ExecuteDeleteAsync(ct);

    /// <summary>For direct chats the conversation id seen by a user is the other participant's user id.</summary>
    public async Task<Guid> ConversationIdForViewerAsync(MlsGroupState g, Guid viewer, CancellationToken ct)
    {
        if (g.GroupChatId is { } gc) return gc;
        var d = await db.DirectDialogs.AsNoTracking().FirstAsync(x => x.Id == g.DialogId, ct);
        return d.User1Id == viewer ? d.User2Id : d.User1Id;
    }

    // ---------- messages ----------

    /// <summary>
    /// Validates that a user message can be stored in the conversation's MLS group:
    /// group exists, the sender device is an active leaf, and the epoch is recent.
    /// </summary>
    public async Task<MlsGroupState> RequireMessageContextAsync(
        Guid senderId, Guid? dialogId, Guid? groupChatId, Guid deviceId, long epoch, byte[]? ciphertext, CancellationToken ct)
    {
        if (ciphertext is not { Length: > 0 and <= MaxMessageBytes })
            throw MlsDeliveryException.Invalid("Сообщение должно быть зашифровано (MLS).");
        var group = await FindGroupAsync(dialogId, groupChatId, ct)
                    ?? throw new MlsDeliveryException("no_group", "Для чата ещё не создана MLS-группа.", 409);
        await RequireActiveDeviceAsync(senderId, deviceId, ct);
        if (!await db.MlsGroupMembers.AnyAsync(m => m.GroupId == group.Id && m.DeviceId == deviceId, ct))
            throw MlsDeliveryException.Forbidden("Устройство не состоит в группе чата.");
        if (epoch > group.Epoch || epoch < group.Epoch - MessageEpochTolerance)
            throw MlsDeliveryException.EpochConflict(group.Epoch);
        return group;
    }

    public MlsGroupEvent AppendMessageEvent(Guid groupId, MlsEventKind kind, Guid messageId, long epoch, Guid senderId, Guid? deviceId)
    {
        var ev = new MlsGroupEvent
        {
            GroupId = groupId,
            Kind = kind,
            Epoch = epoch,
            MessageId = messageId,
            SenderUserId = senderId,
            SenderDeviceId = deviceId,
            CreatedAt = DateTime.UtcNow,
        };
        db.MlsGroupEvents.Add(ev);
        return ev;
    }

    // ---------- maintenance ----------

    /// <summary>Revokes devices inactive longer than <see cref="InactiveDeviceLifetime"/>; returns (device, notify users).</summary>
    public async Task<List<(UserDevice device, List<Guid> notify)>> RevokeInactiveDevicesAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - InactiveDeviceLifetime;
        var stale = await db.UserDevices.Where(d => d.RevokedAt == null && d.LastSeenAt < cutoff).Take(200).ToListAsync(ct);
        var result = new List<(UserDevice, List<Guid>)>();
        foreach (var d in stale)
        {
            var notify = await RevokeDeviceAsync(d.UserId, d.Id, "inactive", ct);
            result.Add((d, notify));
        }
        return result;
    }

    private static string Clean(string? value, int max, string fallback)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length == 0) return fallback;
        return v.Length <= max ? v : v[..max];
    }
}
