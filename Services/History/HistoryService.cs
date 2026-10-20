using System.Globalization;
using System.Security.Cryptography;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Mls;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.History;

public sealed class HistoryException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record VaultDto(long Version, byte[] Keyring, byte[]? PasswordWrap);
public sealed record CreateVaultRequest(Guid DeviceId, byte[] Keyring);
public sealed record UpdateVaultRequest(Guid DeviceId, long ExpectedVersion, byte[] Keyring, byte[]? PasswordWrap);
public sealed record HistoryRecordUploadDto(Guid MessageId, string CiphertextHash, byte[] Blob);
public sealed record UploadHistoryRequest(Guid DeviceId, IReadOnlyList<HistoryRecordUploadDto> Records);
public sealed record UploadHistoryResult(int Stored, IReadOnlyList<Guid> Rejected);
public sealed record HistoryRecordDto(Guid MessageId, byte[] Blob);
public sealed record HistoryPageDto(IReadOnlyList<HistoryRecordDto> Records, string? Next, bool HasMore);
public sealed record CreateAccessRequestRequest(Guid DeviceId, byte[] KeyPackage);
public sealed record AccessRequestDto(Guid Id, Guid DeviceId, string DeviceName, string Platform, DateTime CreatedAt, byte[] KeyPackage);
public sealed record ApproveAccessRequestRequest(Guid DeviceId, byte[] Welcome, byte[] Ciphertext);
public sealed record DeclineAccessRequestRequest(Guid DeviceId);
public sealed record SendGrantRequest(Guid DeviceId, Guid TargetDeviceId, byte[] Welcome, byte[] Ciphertext);
public sealed record GrantDto(Guid Id, Guid FromDeviceId, byte[] Welcome, byte[] Ciphertext);

/// <summary>
/// Storage for multi-device history (see docs/MLS_ARCHITECTURE.md, «Перенос истории»). Holds only sealed blobs:
/// no keys, no text. Every call requires an active device of the user, so a revoked device is cut off.
/// </summary>
public sealed class HistoryService(AppDbContext db, MlsDeliveryService mls)
{
    public const int MaxKeyringBytes = 256 * 1024;
    public const int MaxPasswordWrapBytes = 1024;
    public const int MaxRecordBytes = 512 * 1024;
    public const int MaxBatch = 200;
    public const int MaxPage = 1000;

    public async Task<VaultDto?> GetVaultAsync(Guid me, Guid deviceId, CancellationToken ct)
    {
        await mls.RequireActiveDeviceAsync(me, deviceId, ct);
        var v = await db.HistoryVaults.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me, ct);
        return v is null ? null : new VaultDto(v.Version, v.Keyring, v.PasswordWrap);
    }

    public async Task<long> CreateVaultAsync(Guid me, CreateVaultRequest req, CancellationToken ct)
    {
        ValidateKeyring(req.Keyring);
        await mls.RequireActiveDeviceAsync(me, req.DeviceId, ct);
        if (await db.HistoryVaults.AnyAsync(x => x.UserId == me, ct))
            throw new HistoryException("vault_exists", "Хранилище истории уже создано другим устройством.", 409);
        var now = DateTime.UtcNow;
        db.HistoryVaults.Add(new HistoryVault { UserId = me, Version = 1, Keyring = req.Keyring, UpdatedByDeviceId = req.DeviceId, CreatedAt = now, UpdatedAt = now });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new HistoryException("vault_exists", "Хранилище истории уже создано другим устройством.", 409);
        }
        return 1;
    }

    public async Task<long> UpdateVaultAsync(Guid me, UpdateVaultRequest req, CancellationToken ct)
    {
        ValidateKeyring(req.Keyring);
        if (req.PasswordWrap is { Length: 0 or > MaxPasswordWrapBytes })
            throw new HistoryException("invalid", "Некорректные данные облачного пароля.");
        await mls.RequireActiveDeviceAsync(me, req.DeviceId, ct);
        var now = DateTime.UtcNow;
        var updated = await db.HistoryVaults
            .Where(x => x.UserId == me && x.Version == req.ExpectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Version, x => x.Version + 1)
                .SetProperty(x => x.Keyring, req.Keyring)
                .SetProperty(x => x.PasswordWrap, req.PasswordWrap)
                .SetProperty(x => x.UpdatedByDeviceId, req.DeviceId)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (updated != 1)
            throw new HistoryException("version_conflict", "Хранилище истории изменено с другого устройства.", 409);
        return req.ExpectedVersion + 1;
    }

    private static void ValidateKeyring(byte[]? keyring)
    {
        if (keyring is not { Length: > 0 and <= MaxKeyringBytes })
            throw new HistoryException("invalid", "Некорректное хранилище истории.");
    }

    /// <summary>
    /// Stores records for messages of the user's chats, each only if its hash is the message's current ciphertext
    /// (an upload of a pre-edit version, of a deleted message or of someone else's chat is refused).
    /// </summary>
    public async Task<UploadHistoryResult> UploadAsync(Guid me, UploadHistoryRequest req, CancellationToken ct)
    {
        var records = (req.Records ?? Array.Empty<HistoryRecordUploadDto>()).DistinctBy(r => r.MessageId).ToList();
        if (records.Count == 0) return new UploadHistoryResult(0, Array.Empty<Guid>());
        if (records.Count > MaxBatch) throw new HistoryException("invalid", $"Не больше {MaxBatch} записей за раз.");
        if (records.Any(r => r.Blob is not { Length: > 0 and <= MaxRecordBytes } || r.CiphertextHash is not { Length: 64 }))
            throw new HistoryException("invalid", "Некорректная запись истории.");
        await mls.RequireActiveDeviceAsync(me, req.DeviceId, ct);

        var ids = records.Select(r => r.MessageId).ToList();
        var direct = await db.DirectMessages.AsNoTracking()
            .Where(m => ids.Contains(m.Id) && m.DeletedAt == null && m.Ciphertext != null
                        && db.DirectDialogs.Any(d => d.Id == m.DialogId && (d.User1Id == me || d.User2Id == me)))
            .Select(m => new { m.Id, m.Ciphertext })
            .ToListAsync(ct);
        var group = await db.GroupMessages.AsNoTracking()
            .Where(m => ids.Contains(m.Id) && m.DeletedAt == null && m.Ciphertext != null
                        && db.GroupChatMembers.Any(gm => gm.GroupChatId == m.GroupChatId && gm.UserId == me))
            .Select(m => new { m.Id, m.Ciphertext })
            .ToListAsync(ct);
        var current = direct.Concat(group).ToDictionary(m => m.Id, m => Convert.ToHexString(SHA256.HashData(m.Ciphertext!)));

        var accepted = records.Where(r => current.TryGetValue(r.MessageId, out var h) && string.Equals(h, r.CiphertextHash, StringComparison.OrdinalIgnoreCase)).ToList();
        var rejected = records.Where(r => !accepted.Contains(r)).Select(r => r.MessageId).ToList();
        if (accepted.Count == 0) return new UploadHistoryResult(0, rejected);

        var acceptedIds = accepted.Select(r => r.MessageId).ToList();
        var existing = await db.HistoryRecords.Where(r => r.UserId == me && acceptedIds.Contains(r.MessageId)).ToDictionaryAsync(r => r.MessageId, ct);
        var now = Truncate(DateTime.UtcNow);
        var i = 0;
        foreach (var r in accepted)
        {
            var stamp = now.AddTicks(10 * i++); // distinct microseconds: a stable paging order within the batch
            if (existing.TryGetValue(r.MessageId, out var row))
            {
                if (string.Equals(row.CiphertextHash, r.CiphertextHash, StringComparison.OrdinalIgnoreCase)) continue; // same version already kept
                row.CiphertextHash = r.CiphertextHash.ToUpperInvariant();
                row.Blob = r.Blob;
                row.UpdatedAt = stamp;
            }
            else
            {
                db.HistoryRecords.Add(new HistoryRecord { UserId = me, MessageId = r.MessageId, CiphertextHash = r.CiphertextHash.ToUpperInvariant(), Blob = r.Blob, UpdatedAt = stamp });
            }
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another device of the user uploaded the same messages at the same moment: theirs is as good.
            db.ChangeTracker.Clear();
        }
        return new UploadHistoryResult(accepted.Count, rejected);
    }

    /// <summary>
    /// Records after <paramref name="after"/> (inclusive of the same instant; the client skips what it has), oldest
    /// first. Records of messages deleted since are not returned.
    /// </summary>
    public async Task<HistoryPageDto> GetPageAsync(Guid me, Guid deviceId, string? after, int take, CancellationToken ct)
    {
        await mls.RequireActiveDeviceAsync(me, deviceId, ct);
        take = Math.Clamp(take, 1, MaxPage);
        var from = after is not null && long.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            ? new DateTime(ticks, DateTimeKind.Utc)
            : DateTime.MinValue;

        var rows = await db.HistoryRecords.AsNoTracking()
            .Where(r => r.UserId == me && r.UpdatedAt >= from)
            .OrderBy(r => r.UpdatedAt)
            .Take(take)
            .Select(r => new
            {
                r.MessageId,
                r.Blob,
                r.UpdatedAt,
                Alive = db.DirectMessages.Any(m => m.Id == r.MessageId && m.DeletedAt == null)
                        || db.GroupMessages.Any(m => m.Id == r.MessageId && m.DeletedAt == null),
            })
            .ToListAsync(ct);

        var next = rows.Count == 0 ? after : rows[^1].UpdatedAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        return new HistoryPageDto(rows.Where(r => r.Alive).Select(r => new HistoryRecordDto(r.MessageId, r.Blob)).ToList(), next, rows.Count == take);
    }

    // ---------- device link: a new device gets the history key from an own device ----------

    public static readonly TimeSpan AccessRequestLifetime = TimeSpan.FromMinutes(15);
    public const int MaxTransferBytes = 64 * 1024;
    public const int MaxPendingGrantsPerDevice = 20;

    /// <summary>A new device asks for the key. Earlier pending requests of the same device are replaced.</summary>
    public async Task<HistoryAccessRequest> CreateAccessRequestAsync(Guid me, CreateAccessRequestRequest req, CancellationToken ct)
    {
        if (req.KeyPackage is not { Length: > 0 and <= MlsDeliveryService.MaxKeyPackageBytes })
            throw new HistoryException("invalid", "Некорректный ключ устройства.");
        await mls.RequireActiveDeviceAsync(me, req.DeviceId, ct);
        if (!await db.HistoryVaults.AnyAsync(v => v.UserId == me, ct))
            throw new HistoryException("no_vault", "Истории ещё нет: её не с чего переносить.", 404);

        await db.HistoryAccessRequests
            .Where(r => r.UserId == me && r.DeviceId == req.DeviceId && r.Status == HistoryAccessStatus.Pending)
            .ExecuteDeleteAsync(ct);
        var now = DateTime.UtcNow;
        var request = new HistoryAccessRequest
        {
            UserId = me,
            DeviceId = req.DeviceId,
            KeyPackage = req.KeyPackage,
            Status = HistoryAccessStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now + AccessRequestLifetime,
        };
        db.HistoryAccessRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return request;
    }

    /// <summary>Pending requests of the account's other active devices (for «Новый вход … — это вы?»).</summary>
    public async Task<List<AccessRequestDto>> PendingAccessRequestsAsync(Guid me, Guid deviceId, CancellationToken ct)
    {
        await mls.RequireActiveDeviceAsync(me, deviceId, ct);
        var now = DateTime.UtcNow;
        return await (
            from r in db.HistoryAccessRequests.AsNoTracking()
            join d in db.UserDevices.AsNoTracking() on r.DeviceId equals d.Id
            where r.UserId == me && r.Status == HistoryAccessStatus.Pending && r.ExpiresAt > now
                  && r.DeviceId != deviceId && d.RevokedAt == null && d.UserId == me
            orderby r.CreatedAt
            select new AccessRequestDto(r.Id, r.DeviceId, d.Name, d.Platform, r.CreatedAt, r.KeyPackage)
        ).ToListAsync(ct);
    }

    /// <summary>Status for the requesting device: 0 pending, 1 approved, 2 declined, 3 expired.</summary>
    public async Task<int> AccessRequestStatusAsync(Guid me, Guid deviceId, Guid requestId, CancellationToken ct)
    {
        await mls.RequireActiveDeviceAsync(me, deviceId, ct);
        var r = await db.HistoryAccessRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == requestId && x.UserId == me && x.DeviceId == deviceId, ct)
                ?? throw new HistoryException("not_found", "Запрос не найден.", 404);
        return r.Status == HistoryAccessStatus.Pending && r.ExpiresAt <= DateTime.UtcNow ? 3 : (int)r.Status;
    }

    /// <summary>An own device approved: the request is closed and the grant is queued for the requesting device.</summary>
    public async Task<HistoryAccessRequest> ApproveAccessRequestAsync(Guid me, Guid requestId, ApproveAccessRequestRequest req, CancellationToken ct)
    {
        ValidateTransfer(req.Welcome, req.Ciphertext);
        await mls.RequireActiveDeviceAsync(me, req.DeviceId, ct);
        var r = await RequirePendingAsync(me, requestId, req.DeviceId, ct);
        await mls.RequireActiveDeviceAsync(me, r.DeviceId, ct);
        var now = DateTime.UtcNow;
        var closed = await db.HistoryAccessRequests
            .Where(x => x.Id == r.Id && x.Status == HistoryAccessStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, HistoryAccessStatus.Approved).SetProperty(x => x.ResolvedByDeviceId, req.DeviceId), ct);
        if (closed != 1) throw new HistoryException("already_resolved", "Запрос уже обработан на другом устройстве.", 409);
        db.HistoryGrants.Add(new HistoryGrant { UserId = me, TargetDeviceId = r.DeviceId, FromDeviceId = req.DeviceId, Welcome = req.Welcome, Ciphertext = req.Ciphertext, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        r.Status = HistoryAccessStatus.Approved;
        return r;
    }

    public async Task<HistoryAccessRequest> DeclineAccessRequestAsync(Guid me, Guid requestId, Guid deviceId, CancellationToken ct)
    {
        await mls.RequireActiveDeviceAsync(me, deviceId, ct);
        var r = await RequirePendingAsync(me, requestId, deviceId, ct);
        await db.HistoryAccessRequests
            .Where(x => x.Id == r.Id && x.Status == HistoryAccessStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, HistoryAccessStatus.Declined).SetProperty(x => x.ResolvedByDeviceId, deviceId), ct);
        r.Status = HistoryAccessStatus.Declined;
        return r;
    }

    private async Task<HistoryAccessRequest> RequirePendingAsync(Guid me, Guid requestId, Guid resolverDeviceId, CancellationToken ct)
    {
        var r = await db.HistoryAccessRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == requestId && x.UserId == me, ct)
                ?? throw new HistoryException("not_found", "Запрос не найден.", 404);
        if (r.DeviceId == resolverDeviceId) throw new HistoryException("invalid", "Устройство не может подтвердить само себя.");
        if (r.Status != HistoryAccessStatus.Pending) throw new HistoryException("already_resolved", "Запрос уже обработан на другом устройстве.", 409);
        if (r.ExpiresAt <= DateTime.UtcNow) throw new HistoryException("expired", "Запрос устарел, начните заново на новом устройстве.", 410);
        return r;
    }

    /// <summary>The changed key for another own device (after a revoke), without a request.</summary>
    public async Task SendGrantAsync(Guid me, SendGrantRequest req, CancellationToken ct)
    {
        ValidateTransfer(req.Welcome, req.Ciphertext);
        if (req.TargetDeviceId == req.DeviceId) throw new HistoryException("invalid", "Нельзя отправить ключ самому себе.");
        await mls.RequireActiveDeviceAsync(me, req.DeviceId, ct);
        await mls.RequireActiveDeviceAsync(me, req.TargetDeviceId, ct);
        if (await db.HistoryGrants.CountAsync(g => g.TargetDeviceId == req.TargetDeviceId, ct) >= MaxPendingGrantsPerDevice)
            throw new HistoryException("too_many", "У устройства слишком много непрочитанных ключей.", 429);
        db.HistoryGrants.Add(new HistoryGrant { UserId = me, TargetDeviceId = req.TargetDeviceId, FromDeviceId = req.DeviceId, Welcome = req.Welcome, Ciphertext = req.Ciphertext });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<GrantDto>> GrantsAsync(Guid me, Guid deviceId, CancellationToken ct)
    {
        await mls.RequireActiveDeviceAsync(me, deviceId, ct);
        return await db.HistoryGrants.AsNoTracking()
            .Where(g => g.UserId == me && g.TargetDeviceId == deviceId)
            .OrderBy(g => g.CreatedAt)
            .Select(g => new GrantDto(g.Id, g.FromDeviceId, g.Welcome, g.Ciphertext))
            .ToListAsync(ct);
    }

    public async Task AckGrantAsync(Guid me, Guid deviceId, Guid grantId, CancellationToken ct)
    {
        await mls.RequireActiveDeviceAsync(me, deviceId, ct);
        await db.HistoryGrants.Where(g => g.Id == grantId && g.UserId == me && g.TargetDeviceId == deviceId).ExecuteDeleteAsync(ct);
    }

    private static void ValidateTransfer(byte[]? welcome, byte[]? ciphertext)
    {
        if (welcome is not { Length: > 0 and <= MaxTransferBytes } || ciphertext is not { Length: > 0 and <= MaxTransferBytes })
            throw new HistoryException("invalid", "Некорректные данные передачи.");
    }

    /// <summary>A message was deleted: its archived copies are of no use to anyone.</summary>
    public Task ForgetMessageAsync(Guid messageId, CancellationToken ct) =>
        db.HistoryRecords.Where(r => r.MessageId == messageId).ExecuteDeleteAsync(ct);

    private static DateTime Truncate(DateTime utc) => new(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
}
