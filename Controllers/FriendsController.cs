using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FriendsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<ChatHub> _hub;

    public FriendsController(AppDbContext db, IHubContext<ChatHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    // --------------------------------------------------
    // Helpers
    // --------------------------------------------------
    private Guid MeId
    {
        get
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("No user id in claims.");
            return Guid.Parse(id);
        }
    }

    private static (Guid a, Guid b) OrderPair(Guid x, Guid y) => x < y ? (x, y) : (y, x);

    private Task NotifyFriendRequestsChanged(Guid a, Guid b) =>
        _hub.Clients.Users(a.ToString(), b.ToString()).SendAsync("FriendRequestsChanged");

    private Task NotifyFriendsChanged(Guid a, Guid b) =>
        _hub.Clients.Users(a.ToString(), b.ToString()).SendAsync("FriendsChanged");

    // --------------------------------------------------
    // Список принятых друзей
    // GET /api/friends/list
    // --------------------------------------------------
    [HttpGet("list")]
    public async Task<ActionResult<IEnumerable<FriendDto>>> List(CancellationToken ct)
    {
        var me = MeId;

        var friendIds = await _db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == me || f.AddresseeId == me))
            .Select(f => f.RequesterId == me ? f.AddresseeId : f.RequesterId)
            .Distinct()
            .ToListAsync(ct);

        var friends = await _db.Users
            .AsNoTracking()
            .Where(u => friendIds.Contains(u.Id))
            .OrderBy(u => u.UserName)
            .Select(u => new FriendDto(u.Id, u.UserName, u.Email, u.AvatarUrl))
            .ToListAsync(ct);

        return Ok(friends);
    }

    // --------------------------------------------------
    // Отправить заявку (идемпотентно)
    // POST /api/friends/request/{userId}
    // --------------------------------------------------
    [HttpPost("request/{userId:guid}")]
    public async Task<IActionResult> SendRequest(Guid userId, CancellationToken ct)
    {
        var me = MeId;
        if (me == userId) return BadRequest(new { error = "Cannot befriend yourself." });

        var userExists = await _db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, ct);
        if (!userExists) return NotFound(new { error = "User not found." });

        var existing = await _db.Friendships
            .Where(f =>
                (f.RequesterId == me && f.AddresseeId == userId) ||
                (f.RequesterId == userId && f.AddresseeId == me))
            .OrderByDescending(f => f.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is null)
        {
            var entity = new Friendship
            {
                Id = Guid.NewGuid(),
                RequesterId = me,
                AddresseeId = userId,
                Status = FriendshipStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };
            _db.Friendships.Add(entity);
            await _db.SaveChangesAsync(ct);

            await NotifyFriendRequestsChanged(me, userId);

            return Ok(new
            {
                state = "pending",
                created = true,
                accepted = false,
                requestId = entity.Id
            });
        }

        if (existing.Status == FriendshipStatus.Accepted)
        {
            return Ok(new
            {
                state = "friends",
                created = false,
                accepted = true,
                requestId = existing.Id
            });
        }

        if (existing.Status == FriendshipStatus.Pending &&
            existing.RequesterId == userId && existing.AddresseeId == me)
        {
            existing.Status = FriendshipStatus.Accepted;
            await _db.SaveChangesAsync(ct);

            await NotifyFriendRequestsChanged(me, userId);
            await NotifyFriendsChanged(me, userId);

            return Ok(new
            {
                state = "friends",
                created = false,
                accepted = true,
                autoAccepted = true,
                requestId = existing.Id
            });
        }

        if (existing.Status == FriendshipStatus.Pending &&
            existing.RequesterId == me && existing.AddresseeId == userId)
        {
            return Ok(new
            {
                state = "pending",
                created = false,
                accepted = false,
                requestId = existing.Id
            });
        }

        if (existing.Status == FriendshipStatus.Declined)
        {
            existing.RequesterId = me;
            existing.AddresseeId = userId;
            existing.Status = FriendshipStatus.Pending;
            existing.CreatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await NotifyFriendRequestsChanged(me, userId);

            return Ok(new
            {
                state = "pending",
                created = false,
                reopened = true,
                accepted = false,
                requestId = existing.Id
            });
        }

        return Ok(new
        {
            state = "unknown",
            created = false,
            accepted = false,
            requestId = existing.Id
        });
    }

    // --------------------------------------------------
    // Входящие заявки
    // GET /api/friends/requests/incoming
    // --------------------------------------------------
    [HttpGet("requests/incoming")]
    public async Task<ActionResult<IEnumerable<FriendRequestDto>>> IncomingRequests(CancellationToken ct)
    {
        var me = MeId;

        var list = await _db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Pending && f.AddresseeId == me)
            .OrderByDescending(f => f.CreatedAt)
            .Join(_db.Users.AsNoTracking(),
                  f => f.RequesterId,
                  u => u.Id,
                  (f, u) => new FriendRequestDto(
                      f.Id,
                      u.Id,
                      u.UserName,
                      u.Email,
                      f.CreatedAt,
                      "incoming"))
            .ToListAsync(ct);

        return Ok(list);
    }

    // --------------------------------------------------
    // Исходящие заявки
    // GET /api/friends/requests/outgoing
    // --------------------------------------------------
    [HttpGet("requests/outgoing")]
    public async Task<ActionResult<IEnumerable<FriendRequestDto>>> OutgoingRequests(CancellationToken ct)
    {
        var me = MeId;

        var list = await _db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Pending && f.RequesterId == me)
            .OrderByDescending(f => f.CreatedAt)
            .Join(_db.Users.AsNoTracking(),
                  f => f.AddresseeId,
                  u => u.Id,
                  (f, u) => new FriendRequestDto(
                      f.Id,
                      u.Id,
                      u.UserName,
                      u.Email,
                      f.CreatedAt,
                      "outgoing"))
            .ToListAsync(ct);

        return Ok(list);
    }

    // --------------------------------------------------
    // Принять заявку
    // POST /api/friends/requests/{requestId}/accept
    // --------------------------------------------------
    [HttpPost("requests/{requestId:guid}/accept")]
    public async Task<IActionResult> Accept(Guid requestId, CancellationToken ct)
    {
        var me = MeId;

        var req = await _db.Friendships.FirstOrDefaultAsync(f =>
            f.Id == requestId &&
            f.Status == FriendshipStatus.Pending &&
            f.AddresseeId == me, ct);

        if (req is null) return NotFound(new { error = "Request not found." });

        req.Status = FriendshipStatus.Accepted;
        await _db.SaveChangesAsync(ct);

        await NotifyFriendRequestsChanged(me, req.RequesterId);
        await NotifyFriendsChanged(me, req.RequesterId);

        return Ok(new { ok = true });
    }

    // --------------------------------------------------
    // Отклонить заявку
    // POST /api/friends/requests/{requestId}/decline
    // --------------------------------------------------
    [HttpPost("requests/{requestId:guid}/decline")]
    public async Task<IActionResult> Decline(Guid requestId, CancellationToken ct)
    {
        var me = MeId;

        var req = await _db.Friendships.FirstOrDefaultAsync(f =>
            f.Id == requestId &&
            f.Status == FriendshipStatus.Pending &&
            f.AddresseeId == me, ct);

        if (req is null) return NotFound(new { error = "Request not found." });

        req.Status = FriendshipStatus.Declined;
        await _db.SaveChangesAsync(ct);

        await NotifyFriendRequestsChanged(me, req.RequesterId);

        return Ok(new { ok = true });
    }

    // --------------------------------------------------
    // Отменить свою исходящую
    // DELETE /api/friends/requests/{requestId}
    // --------------------------------------------------
    [HttpDelete("requests/{requestId:guid}")]
    public async Task<IActionResult> Cancel(Guid requestId, CancellationToken ct)
    {
        var me = MeId;

        var req = await _db.Friendships.FirstOrDefaultAsync(f =>
            f.Id == requestId &&
            f.Status == FriendshipStatus.Pending &&
            f.RequesterId == me, ct);

        if (req is null) return NotFound(new { error = "Request not found." });

        var otherId = req.AddresseeId;

        _db.Friendships.Remove(req);
        await _db.SaveChangesAsync(ct);

        await NotifyFriendRequestsChanged(me, otherId);

        return Ok(new { ok = true });
    }

    // --------------------------------------------------
    // Удалить друга (+ опционально очистить историю)
    // DELETE /api/friends/{friendId}?clearChatHistory=true|false
    // --------------------------------------------------
    [HttpDelete("{friendId:guid}")]
    public async Task<IActionResult> RemoveFriend(
        Guid friendId,
        [FromQuery] bool clearChatHistory = false,
        CancellationToken ct = default)
    {
        var me = MeId;
        if (friendId == me) return BadRequest(new { error = "Cannot remove yourself." });

        // Должны быть друзья (Accepted) — иначе не даём "удалять друга".
        var acceptedExists = await _db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == me && f.AddresseeId == friendId) ||
             (f.RequesterId == friendId && f.AddresseeId == me)), ct);

        if (!acceptedExists)
            return NotFound(new { error = "Friendship not found." });

        // Удаляем все записи дружбы/заявок между парой (чтобы не оставалось хвостов Pending/Declined).
        var allBetween = await _db.Friendships
            .Where(f =>
                (f.RequesterId == me && f.AddresseeId == friendId) ||
                (f.RequesterId == friendId && f.AddresseeId == me))
            .ToListAsync(ct);

        _db.Friendships.RemoveRange(allBetween);

        // Опционально чистим историю ЛС: messages + dialog
        bool chatCleared = false;
        if (clearChatHistory)
        {
            var (u1, u2) = OrderPair(me, friendId);

            var dlg = await _db.DirectDialogs
                .FirstOrDefaultAsync(d => d.User1Id == u1 && d.User2Id == u2, ct);

            if (dlg is not null)
            {
                // EF Core 8: быстрый bulk delete без загрузки всех сообщений в память
                await _db.DirectMessages
                    .Where(m => m.DialogId == dlg.Id)
                    .ExecuteDeleteAsync(ct);

                _db.DirectDialogs.Remove(dlg);
                chatCleared = true;
            }
        }

        await _db.SaveChangesAsync(ct);

        // Синхронизация клиента
        await NotifyFriendsChanged(me, friendId);
        await NotifyFriendRequestsChanged(me, friendId);

        return Ok(new
        {
            ok = true,
            clearedChat = chatCleared
        });
    }
}
