using System.Collections.Generic;
using System.Threading.Tasks;

namespace JaeZoo.Server.Hubs
{
    public interface IPresenceTracker
    {
        Task<bool> UserConnected(string userId, string connectionId);
        Task<bool> UserDisconnected(string userId, string connectionId);
        Task<List<string>> GetOnlineUsers();
    }
}
