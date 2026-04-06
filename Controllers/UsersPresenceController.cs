using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
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
        public bool ShowOnline { get; set; }
    }

    // GET api/users/presence-visibility
    [HttpGet("presence-visibility")]
    public async Task<ActionResult<PresenceVisibilityDto>> GetVisibility()
    {
        var val = await _db.Users.Where(u => u.Id == MeId).Select(u => u.ShowOnline).FirstOrDefaultAsync();
        return Ok(new PresenceVisibilityDto { ShowOnline = val });
    }

    // PUT api/users/presence-visibility
    [HttpPut("presence-visibility")]
    public async Task<IActionResult> SetVisibility([FromBody] PresenceVisibilityDto dto)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == MeId);
        if (user == null) return Unauthorized();

        var old = user.ShowOnline;
        user.ShowOnline = dto.ShowOnline;
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

        return NoContent();
    }
}
