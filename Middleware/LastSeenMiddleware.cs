using System.Collections.Concurrent;
using System.Security.Claims;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Middleware
{
    /// <summary>
    /// Updates User.LastSeen at most once a minute per user. The write runs in the background with one UPDATE,
    /// so it never adds database round trips to the request that triggered it.
    /// </summary>
    public class LastSeenMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<LastSeenMiddleware> _log;
        private static readonly ConcurrentDictionary<Guid, DateTime> _last = new();
        private static readonly TimeSpan _ttl = TimeSpan.FromSeconds(60);

        public LastSeenMiddleware(RequestDelegate next, IServiceScopeFactory scopes, ILogger<LastSeenMiddleware> log)
        {
            _next = next;
            _scopes = scopes;
            _log = log;
        }

        public async Task Invoke(HttpContext ctx)
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
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var scope = _scopes.CreateScope();
                                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                                await db.Users.Where(u => u.Id == uid).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeen, now));
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "LastSeen update failed for {UserId}", uid);
                            }
                        });
                    }
                }
            }

            await _next(ctx);
        }
    }
}
