using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersPresenceController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPresenceTracker _presence;
    private readonly IHubContext<ChatHub> _hub;

    public UsersPresenceController(AppDbContext db, IPresenceTracker presence, IHubContext<ChatHub> hub)
    {
        _db = db;
        _presence = presence;
        _hub = hub;
    }

    private Guid MeId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public sealed class PresenceVisibilityDto
    {
        public bool ShowOnline { get; set; } = true;
        public LastSeenVisibility LastSeenVisibility { get; set; } = LastSeenVisibility.Approximate;
        public bool ShowActivity { get; set; } = true;
    }

    // GET api/users/presence-visibility
    [HttpGet("presence-visibility")]
    public async Task<ActionResult<PresenceVisibilityDto>> GetVisibility()
    {
        var dto = await _db.Users
            .Where(u => u.Id == MeId)
            .Select(u => new PresenceVisibilityDto
            {
                ShowOnline = u.ShowOnline,
                LastSeenVisibility = u.LastSeenVisibility,
                ShowActivity = u.ShowActivity
            })
            .FirstOrDefaultAsync();

        return Ok(dto ?? new PresenceVisibilityDto());
    }

    // PUT api/users/presence-visibility
    [HttpPut("presence-visibility")]
    public async Task<IActionResult> SetVisibility([FromBody] PresenceVisibilityDto dto)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == MeId);
        if (user == null) return Unauthorized();

        var old = user.ShowOnline;
        user.ShowOnline = dto.ShowOnline;
        user.LastSeenVisibility = dto.LastSeenVisibility;
        user.ShowActivity = dto.ShowActivity;
        if (!user.ShowActivity)
        {
            user.CurrentActivityName = null;
            user.CurrentActivityUpdatedAt = DateTime.UtcNow;
        }
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Если юзер сейчас подключён — уведомим остальных о смене видимости.
        // Проверим наличие в PresenceTracker.
        var onlineIds = await _presence.GetOnlineUsers();
        var meStr = MeId.ToString();

        if (onlineIds.Contains(meStr))
        {
            if (old && !dto.ShowOnline)
            {
                // Был видимым → стал невидимым → обозначим "ушёл"
                await _hub.Clients.All.SendAsync("UserOffline", meStr);
            }
            else if (!old && dto.ShowOnline)
            {
                // Был невидимым → стал видимым → обозначим "пришёл"
                await _hub.Clients.All.SendAsync("UserOnline", meStr);
            }
        }

        if (!user.ShowActivity)
        {
            var friendIds = await _db.Friendships.AsNoTracking()
                .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == MeId || f.AddresseeId == MeId))
                .Select(f => f.RequesterId == MeId ? f.AddresseeId : f.RequesterId)
                .Distinct()
                .ToListAsync();

            foreach (var friendId in friendIds)
            {
                await _hub.Clients.User(friendId.ToString()).SendAsync("UserActivityChanged", new
                {
                    userId = MeId.ToString("D"),
                    activityName = (string?)null,
                    updatedAt = user.CurrentActivityUpdatedAt
                });
            }
        }

        return NoContent();
    }

    // POST api/users/activity
    [HttpPost("activity")]
    public async Task<IActionResult> SetActivity([FromBody] UserActivityRequest dto)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == MeId);
        if (user == null) return Unauthorized();

        var activity = (dto?.ActivityName ?? string.Empty).Trim();
        if (activity.Length > 96)
            activity = activity[..96].Trim();

        user.CurrentActivityName = user.ShowActivity && !string.IsNullOrWhiteSpace(activity) ? activity : null;
        user.CurrentActivityUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var friendIds = await _db.Friendships.AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == MeId || f.AddresseeId == MeId))
            .Select(f => f.RequesterId == MeId ? f.AddresseeId : f.RequesterId)
            .Distinct()
            .ToListAsync();

        foreach (var friendId in friendIds)
        {
            await _hub.Clients.User(friendId.ToString()).SendAsync("UserActivityChanged", new
            {
                userId = MeId.ToString("D"),
                activityName = user.CurrentActivityName,
                updatedAt = user.CurrentActivityUpdatedAt
            });
        }

        return NoContent();
    }

}
