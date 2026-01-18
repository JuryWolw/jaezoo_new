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
            // гонка: диалог уже создали
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

    /// <summary>
    /// История сообщений с другом.
    /// Совместимость: skip/take (старый режим).
    /// Надёжный курсор: before/after + beforeId/afterId (новый режим).
    /// </summary>
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
            // 1) friend должен существовать, иначе возможен FK/DbUpdateException внутри GetOrCreateDialog
            var friendExists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == friendId, ct);
            if (!friendExists) return NotFound(new { error = "Friend not found." });

            // 2) доступ только для друзей
            if (!await AreFriends(MeId, friendId, ct)) return Forbid();

            // 3) диалог может создаваться в гонке / может упасть — history не должен убивать клиента
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

            // ===== КУРСОРНЫЙ РЕЖИМ =====
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

                var itemsCursor = await q
                    .OrderByDescending(m => m.SentAt)
                    .ThenByDescending(m => m.Id)
                    .Take(Math.Clamp(take, 1, 200))
                    .Select(m => new MessageDto(m.Id, m.SenderId, m.Text, m.SentAt))
                    .ToListAsync(ct);

                return Ok(itemsCursor);
            }

            // ===== СТАРЫЙ РЕЖИМ =====
            var items = await q
                .OrderBy(m => m.SentAt)
                .ThenBy(m => m.Id)
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, 200))
                .Select(m => new MessageDto(m.Id, m.SenderId, m.Text, m.SentAt))
                .ToListAsync(ct);

            return Ok(items);
        }
        catch (Exception ex)
        {
            // Железно: history не должен отдавать 500, иначе клиент будет падать при открытии чата
            log.LogError(ex, "History failed: me={MeId}, friend={FriendId}", MeId, friendId);
            return Ok(Array.Empty<MessageDto>());
        }
    }

    /// <summary>
    /// Сводка непрочитанных по всем диалогам текущего пользователя.
    /// </summary>
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
                // friend может быть удалён/битый FK → не валим
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

    /// <summary>
    /// Отметить диалог прочитанным до заданного курсора.
    /// </summary>
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
                // клиенту не надо падать — просто ок
                return Ok(new { ok = true });
            }

            var (curAt, curId) = GetReadCursor(dlg, me);

            // обновляем только если курсор двигается вперёд
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
            // Не валим клиента
            return Ok(new { ok = true });
        }
    }
}
