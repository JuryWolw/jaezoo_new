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

    /// <summary>A message was deleted: its archived copies are of no use to anyone.</summary>
    public Task ForgetMessageAsync(Guid messageId, CancellationToken ct) =>
        db.HistoryRecords.Where(r => r.MessageId == messageId).ExecuteDeleteAsync(ct);

    private static DateTime Truncate(DateTime utc) => new(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
}
