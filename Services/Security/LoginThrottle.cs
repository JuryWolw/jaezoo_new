using System.Collections.Concurrent;

namespace JaeZoo.Server.Services.Security;

/// <summary>
/// Ограничение подбора: после серии неудачных попыток вход по аккаунту (или с адреса) временно закрыт.
/// Ключ аккаунта — хэш логина/почты из запроса, поэтому несуществующий аккаунт ведёт себя так же, как настоящий
/// (по ответу нельзя узнать, есть ли такой). Хранится в памяти: сервер один, при перезапуске счётчики обнуляются.
/// </summary>
public sealed class LoginThrottle
{
    public const int AccountLimit = 5;
    public const int AddressLimit = 30;
    public const int TwoFactorLimit = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private sealed class Counter
    {
        public int Failures;
        public DateTime WindowStart;
        public DateTime? LockedUntil;
    }

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _now;

    public LoginThrottle() : this(() => DateTime.UtcNow) { }

    public LoginThrottle(Func<DateTime> now) => _now = now;

    public static string AccountKey(string lookupHash) => "acc:" + lookupHash;
    public static string AddressKey(string? ip) => "ip:" + (ip ?? "unknown");
    public static string TwoFactorKey(Guid userId) => "2fa:" + userId.ToString("N");

    /// <summary>Сколько ещё ждать, если ключ сейчас закрыт (null — можно пробовать).</summary>
    public TimeSpan? RetryAfter(string key)
    {
        if (!_counters.TryGetValue(key, out var c)) return null;
        lock (c)
        {
            var now = _now();
            if (c.LockedUntil is { } until && until > now) return until - now;
            return null;
        }
    }

    public TimeSpan? RetryAfterAny(params string[] keys)
    {
        TimeSpan? longest = null;
        foreach (var key in keys)
            if (RetryAfter(key) is { } wait && (longest is null || wait > longest))
                longest = wait;
        return longest;
    }

    public void RegisterFailure(string key, int limit)
    {
        var c = _counters.GetOrAdd(key, _ => new Counter { WindowStart = _now() });
        lock (c)
        {
            var now = _now();
            if (now - c.WindowStart > Window)
            {
                c.WindowStart = now;
                c.Failures = 0;
            }
            c.Failures++;
            if (c.Failures >= limit)
            {
                c.LockedUntil = now + Lockout;
                c.Failures = 0;
                c.WindowStart = now;
            }
        }
        PruneOccasionally();
    }

    public void Reset(string key) => _counters.TryRemove(key, out _);

    private int _calls;
    private void PruneOccasionally()
    {
        if (Interlocked.Increment(ref _calls) % 512 != 0) return;
        var now = _now();
        foreach (var (key, c) in _counters)
        {
            lock (c)
            {
                if ((c.LockedUntil is null || c.LockedUntil <= now) && now - c.WindowStart > Window)
                    _counters.TryRemove(key, out _);
            }
        }
    }
}
