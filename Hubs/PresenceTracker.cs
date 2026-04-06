using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JaeZoo.Server.Hubs
{
    public class PresenceTracker : IPresenceTracker
    {
        private static readonly ConcurrentDictionary<string, HashSet<string>> _online =
            new ConcurrentDictionary<string, HashSet<string>>();

        public Task<bool> UserConnected(string userId, string connectionId)
        {
            var set = _online.GetOrAdd(userId, _ => new HashSet<string>());
            lock (set)
            {
                set.Add(connectionId);
                return Task.FromResult(set.Count == 1);
            }
        }

        public Task<bool> UserDisconnected(string userId, string connectionId)
        {
            if (!_online.TryGetValue(userId, out var set))
                return Task.FromResult(false);

            lock (set)
            {
                set.Remove(connectionId);
                if (set.Count == 0)
                {
                    _online.TryRemove(userId, out _);
                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            }
        }

        public Task<List<string>> GetOnlineUsers()
        {
            var ids = _online.Keys.OrderBy(x => x).ToList();
            return Task.FromResult(ids);
        }
    }
}
