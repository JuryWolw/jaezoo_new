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
public class ChatController(AppDbContext db) : ControllerBase
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

    private async Task<DirectDialog> GetOrCreateDialog(Guid aId, Guid bId)
    {
        var (u1, u2) = OrderPair(aId, bId);

        var dlg = await db.DirectDialogs.FirstOrDefaultAsync(d => d.User1Id == u1 && d.User2Id == u2);
        if (dlg is not null) return dlg;

        dlg = new DirectDialog { User1Id = u1, User2Id = u2 };
        db.DirectDialogs.Add(dlg);

        try
        {
            await db.SaveChangesAsync();
            return dlg;
        }
        catch (DbUpdateException)
        {
            // гонка: диалог уже создали
            var existing = await db.DirectDialogs.FirstOrDefaultAsync(d => d.User1Id == u1 && d.User2Id == u2);
            if (existing is not null) return existing;
            throw;
        }
    }

    private Task<bool> AreFriends(Guid me, Guid other) =>
        db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == me && f.AddresseeId == other) ||
             (f.RequesterId == other && f.AddresseeId == me)));

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
        Guid? afterId = null
    )
    {
        if (!await AreFriends(MeId, friendId)) return Forbid();

        var dlg = await GetOrCreateDialog(MeId, friendId);

        var q = db.DirectMessages
            .AsNoTracking()
            .Where(m => m.DialogId == dlg.Id);

        // ===== КУРСОРНЫЙ РЕЖИМ =====
        // ВАЖНО: композитный курсор (SentAt, Id) чтобы не "терять" сообщения с одинаковым SentAt.
        if (before.HasValue || after.HasValue)
        {
            if (before.HasValue)
            {
                var bt = DateTime.SpecifyKind(before.Value, DateTimeKind.Utc);

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
                var at = DateTime.SpecifyKind(after.Value, DateTimeKind.Utc);

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

            // Возвращаем "последние" (DESC) — удобно для пагинации вверх
            var itemsCursor = await q
                .OrderByDescending(m => m.SentAt)
                .ThenByDescending(m => m.Id)
                .Take(Math.Clamp(take, 1, 200))
                .Select(m => new MessageDto(m.SenderId, m.Text, m.SentAt))
                .ToListAsync();

            return Ok(itemsCursor);
        }

        // ===== СТАРЫЙ РЕЖИМ (совместимость): skip/take =====
        var items = await q
            .OrderBy(m => m.SentAt)
            .ThenBy(m => m.Id)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200))
            .Select(m => new MessageDto(m.SenderId, m.Text, m.SentAt))
            .ToListAsync();

        return Ok(items);
    }
}
