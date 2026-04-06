using System.Collections.Concurrent;
using System.Security.Claims;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Middleware
{
    public class LastSeenMiddleware
    {
        private readonly RequestDelegate _next;
        private static readonly ConcurrentDictionary<Guid, DateTime> _last = new();
        private static readonly TimeSpan _ttl = TimeSpan.FromSeconds(60);

        public LastSeenMiddleware(RequestDelegate next) => _next = next;

        public async Task Invoke(HttpContext ctx, AppDbContext db)
        {
            if (ctx.User?.Identity?.IsAuthenticated == true)
            {
                var idStr = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (Guid.TryParse(idStr, out var uid))
                {
                    var now = DateTime.UtcNow;
                    var when = _last.GetOrAdd(uid, now.AddDays(-1));
                    if (now - when >= _ttl)
                    {
                        _last[uid] = now;
                        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid);
                        if (user != null)
                        {
                            user.LastSeen = now;
                            await db.SaveChangesAsync();
                        }
                    }
                }
            }

            await _next(ctx);
        }
    }
}
