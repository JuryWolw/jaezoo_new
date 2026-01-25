using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ChatController(AppDbContext db, ILogger<ChatController> log) : ControllerBase
{
    private Guid MeId
    {
        get
        {
            var s = User.FindFirst("sub")?.Value
                    ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst("uid")?.Value;

            if (!Guid.TryParse(s, out var id))
                throw new UnauthorizedAccessException("No user id claim.");

            return id;
        }
    }

    private static (Guid a, Guid b) OrderPair(Guid x, Guid y) => x < y ? (x, y) : (y, x);

    private async Task<DirectDialog> GetOrCreateDialog(Guid aId, Guid bId, CancellationToken ct = default)
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

    private Task<bool> AreFriends(Guid me, Guid other, CancellationToken ct = default) =>
        db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == me && f.AddresseeId == other) ||
             (f.RequesterId == other && f.AddresseeId == me)), ct);

    private static (DateTime at, Guid id) GetReadCursor(DirectDialog dlg, Guid me)
    {
        if (me == dlg.User1Id) return (dlg.LastReadAtUser1, dlg.LastReadMessageIdUser1);
        if (me == dlg.User2Id) return (dlg.LastReadAtUser2, dlg.LastReadMessageIdUser2);
        return (DateTime.MinValue, Guid.Empty);
    }

    private static void SetReadCursor(DirectDialog dlg, Guid me, DateTime atUtc, Guid msgId)
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

    private static DateTime EnsureUtc(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Utc) return dt;
        if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    private static bool IsImage(string? ct) =>
        !string.IsNullOrWhiteSpace(ct) && ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideo(string? ct) =>
        !string.IsNullOrWhiteSpace(ct) && ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static AttachmentDto ToAttachmentDto(ChatFile f) => new(
        f.Id,
        f.OriginalFileName,
        f.ContentType,
        f.SizeBytes,
        $"/api/files/{f.Id}",
        IsImage(f.ContentType),
        IsVideo(f.ContentType)
    );

    [HttpGet("history/{friendId:guid}")]
    public async Task<ActionResult<IEnumerable<MessageDto>>> History(
        Guid friendId,
        int skip = 0,
        int take = 50,
        DateTime? before = null,
        Guid? beforeId = null,
        DateTime? after = null,
        Guid? afterId = null,
        CancellationToken ct = default
    )
    {
        try
        {
            var friendExists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == friendId, ct);
            if (!friendExists) return NotFound(new { error = "Friend not found." });

            if (!await AreFriends(MeId, friendId, ct)) return Forbid();

            DirectDialog dlg;
            try
            {
                dlg = await GetOrCreateDialog(MeId, friendId, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "GetOrCreateDialog failed: me={MeId}, friend={FriendId}", MeId, friendId);
                return Ok(Array.Empty<MessageDto>());
            }

            var q = db.DirectMessages
                .AsNoTracking()
                .Where(m => m.DialogId == dlg.Id);

            // ===== cursor mode =====
            if (before.HasValue || after.HasValue)
            {
                if (before.HasValue)
                {
                    var bt = EnsureUtc(before.Value);
                    if (beforeId.HasValue)
                    {
                        var bid = beforeId.Value;
                        q = q.Where(m => m.SentAt < bt || (m.SentAt == bt && m.Id.CompareTo(bid) < 0));
                    }
                    else
                    {
                        q = q.Where(m => m.SentAt < bt);
                    }
                }

                if (after.HasValue)
                {
                    var at = EnsureUtc(after.Value);
                    if (afterId.HasValue)
                    {
                        var aid = afterId.Value;
                        q = q.Where(m => m.SentAt > at || (m.SentAt == at && m.Id.CompareTo(aid) > 0));
                    }
                    else
                    {
                        q = q.Where(m => m.SentAt > at);
                    }
                }

                var rows = await q
                    .OrderByDescending(m => m.SentAt)
                    .ThenByDescending(m => m.Id)
                    .Take(Math.Clamp(take, 1, 200))
                    .Select(m => new { m.Id, m.SenderId, m.Text, m.SentAt })
                    .ToListAsync(ct);

                var msgIds = rows.Select(r => r.Id).ToList();
                var att = await LoadAttachmentsForMessages(msgIds, ct);

                var items = rows.Select(r =>
                    new MessageDto(r.Id, r.SenderId, r.Text, r.SentAt,
                        att.TryGetValue(r.Id, out var list) ? list : null
                    )
                ).ToList();

                return Ok(items);
            }

            // ===== old mode (skip/take) =====
            var rowsOld = await q
                .OrderBy(m => m.SentAt)
                .ThenBy(m => m.Id)
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, 200))
                .Select(m => new { m.Id, m.SenderId, m.Text, m.SentAt })
                .ToListAsync(ct);

            var msgIdsOld = rowsOld.Select(r => r.Id).ToList();
            var attOld = await LoadAttachmentsForMessages(msgIdsOld, ct);

            var itemsOld = rowsOld.Select(r =>
                new MessageDto(r.Id, r.SenderId, r.Text, r.SentAt,
                    attOld.TryGetValue(r.Id, out var list) ? list : null
                )
            ).ToList();

            return Ok(itemsOld);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "History failed: me={MeId}, friend={FriendId}", MeId, friendId);
            return Ok(Array.Empty<MessageDto>());
        }
    }

    private async Task<Dictionary<Guid, List<AttachmentDto>>> LoadAttachmentsForMessages(List<Guid> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0)
            return new();

        var rows = await (
            from a in db.DirectMessageAttachments.AsNoTracking()
            join f in db.ChatFiles.AsNoTracking() on a.FileId equals f.Id
            where messageIds.Contains(a.MessageId)
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

        // стабильный порядок вложений
        foreach (var kv in map)
            kv.Value.Sort((x, y) => string.CompareOrdinal(x.FileName, y.FileName));

        return map;
    }

    [HttpGet("unread")]
    public async Task<ActionResult<IEnumerable<UnreadDialogDto>>> UnreadSummary(CancellationToken ct)
    {
        try
        {
            var me = MeId;

            var friendIds = await db.Friendships
                .AsNoTracking()
                .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == me || f.AddresseeId == me))
                .Select(f => f.RequesterId == me ? f.AddresseeId : f.RequesterId)
                .Distinct()
                .ToListAsync(ct);

            var result = new List<UnreadDialogDto>(friendIds.Count);

            foreach (var friendId in friendIds)
            {
                var friendExists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == friendId, ct);
                if (!friendExists)
                {
                    result.Add(new UnreadDialogDto(friendId, 0, null, null));
                    continue;
                }

                DirectDialog dlg;
                try
                {
                    dlg = await GetOrCreateDialog(me, friendId, ct);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "GetOrCreateDialog failed in UnreadSummary: me={MeId}, friend={FriendId}", me, friendId);
                    result.Add(new UnreadDialogDto(friendId, 0, null, null));
                    continue;
                }

                var (at, id) = GetReadCursor(dlg, me);

                var q = db.DirectMessages
                    .AsNoTracking()
                    .Where(m => m.DialogId == dlg.Id && m.SenderId != me)
                    .Where(m => m.SentAt > at || (m.SentAt == at && m.Id.CompareTo(id) > 0));

                var count = await q.CountAsync(ct);
                if (count == 0)
                {
                    result.Add(new UnreadDialogDto(friendId, 0, null, null));
                    continue;
                }

                var first = await q
                    .OrderBy(m => m.SentAt)
                    .ThenBy(m => m.Id)
                    .Select(m => new { m.Id, m.SentAt })
                    .FirstOrDefaultAsync(ct);

                result.Add(new UnreadDialogDto(friendId, count, first?.Id, first?.SentAt));
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "UnreadSummary failed: me={MeId}", MeId);
            return Ok(Array.Empty<UnreadDialogDto>());
        }
    }

    [HttpPost("mark-read/{friendId:guid}")]
    public async Task<IActionResult> MarkRead(Guid friendId, [FromBody] MarkReadRequest body, CancellationToken ct)
    {
        try
        {
            var me = MeId;

            var friendExists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == friendId, ct);
            if (!friendExists) return NotFound(new { error = "Friend not found." });

            if (!await AreFriends(me, friendId, ct)) return Forbid();
            if (body is null) return BadRequest(new { error = "Body is required." });

            var mid = body.LastReadMessageId;
            if (mid == Guid.Empty) return BadRequest(new { error = "LastReadMessageId is required." });

            var at = EnsureUtc(body.LastReadAt);

            DirectDialog dlg;
            try
            {
                dlg = await GetOrCreateDialog(me, friendId, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "GetOrCreateDialog failed in MarkRead: me={MeId}, friend={FriendId}", me, friendId);
                return Ok(new { ok = true });
            }

            var (curAt, curId) = GetReadCursor(dlg, me);

            if (at > curAt || (at == curAt && mid.CompareTo(curId) > 0))
            {
                SetReadCursor(dlg, me, at, mid);
                await db.SaveChangesAsync(ct);
            }

            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "MarkRead failed: me={MeId}, friend={FriendId}", MeId, friendId);
            return Ok(new { ok = true });
        }
    }
}
