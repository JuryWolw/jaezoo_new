using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using JaeZoo.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JaeZoo.Server.Security;

/// <summary>
/// In-memory cache of what every authenticated request checks: the user's token version, security stamp,
/// disabled flag and e-mail confirmation, and whether the session is active. Without it each request paid
/// two database round trips (≈200 ms each between Render and the database) before doing any work.
///
/// Correctness: any change of a <see cref="User"/> or <see cref="UserSession"/> saved through EF drops the
/// entries (<see cref="AuthStateInvalidationInterceptor"/>); bulk updates call <see cref="InvalidateUser"/>
/// explicitly. Entries also expire after <see cref="Ttl"/>, which bounds staleness if the server ever runs
/// as several instances (each instance invalidates only its own cache).
/// </summary>
public sealed class AuthStateCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private const int MaxEntries = 50_000;

    public sealed record UserState(int TokenVersion, string SecurityStamp, bool IsDisabled, bool EmailConfirmed);

    private sealed record Entry<T>(T Value, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<Guid, Entry<UserState>> _users = new();
    private readonly ConcurrentDictionary<Guid, Entry<(Guid UserId, DateTime SessionExpiresAt)>> _sessions = new();

    public bool TryGetUser(Guid userId, out UserState state)
    {
        if (_users.TryGetValue(userId, out var e) && e.ExpiresAt > DateTime.UtcNow)
        {
            state = e.Value;
            return true;
        }
        state = null!;
        return false;
    }

    public void SetUser(Guid userId, UserState state)
    {
        TrimIfLarge(_users);
        _users[userId] = new Entry<UserState>(state, DateTime.UtcNow + Ttl);
    }

    /// <summary>True only for a session known to be active (not revoked, not expired).</summary>
    public bool IsSessionKnownActive(Guid sessionId, Guid userId)
    {
        var now = DateTime.UtcNow;
        return _sessions.TryGetValue(sessionId, out var e)
               && e.ExpiresAt > now
               && e.Value.UserId == userId
               && e.Value.SessionExpiresAt > now;
    }

    public void SetSessionActive(Guid sessionId, Guid userId, DateTime sessionExpiresAt)
    {
        TrimIfLarge(_sessions);
        _sessions[sessionId] = new Entry<(Guid, DateTime)>((userId, sessionExpiresAt), DateTime.UtcNow + Ttl);
    }

    public void InvalidateUser(Guid userId)
    {
        _users.TryRemove(userId, out _);
        foreach (var kv in _sessions)
        {
            if (kv.Value.Value.UserId == userId)
                _sessions.TryRemove(kv.Key, out _);
        }
    }

    public void InvalidateSession(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    private static void TrimIfLarge<T>(ConcurrentDictionary<Guid, Entry<T>> map)
    {
        if (map.Count < MaxEntries) return;
        var now = DateTime.UtcNow;
        foreach (var kv in map)
        {
            if (kv.Value.ExpiresAt <= now)
                map.TryRemove(kv.Key, out _);
        }
        if (map.Count >= MaxEntries)
            map.Clear();
    }
}

/// <summary>
/// Drops cached auth state for every User / UserSession that EF saves: once before the save and once more after it,
/// so a request that re-cached the old row while the save was in flight does not keep it.
/// </summary>
public sealed class AuthStateInvalidationInterceptor(AuthStateCache cache) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<(Guid Id, bool IsUser)>> _pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Before(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Before(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        After(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        After(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => After(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        After(eventData.Context);
        return Task.CompletedTask;
    }

    private void Before(DbContext? context)
    {
        if (context is null) return;
        List<(Guid, bool)>? touched = null;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
                continue;
            switch (entry.Entity)
            {
                case User u:
                    (touched ??= new()).Add((u.Id, true));
                    break;
                case UserSession s:
                    (touched ??= new()).Add((s.Id, false));
                    break;
            }
        }
        if (touched is null) return;
        Apply(touched);
        _pending.AddOrUpdate(context, touched);
    }

    private void After(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var touched)) return;
        _pending.Remove(context);
        Apply(touched);
    }

    private void Apply(List<(Guid Id, bool IsUser)> touched)
    {
        foreach (var (id, isUser) in touched)
        {
            if (isUser) cache.InvalidateUser(id);
            else cache.InvalidateSession(id);
        }
    }
}
