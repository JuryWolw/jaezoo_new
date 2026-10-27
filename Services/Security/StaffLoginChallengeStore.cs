using System.Collections.Concurrent;

namespace JaeZoo.Server.Services.Security;

/// <summary>
/// Незавершённые входы персонала (пароль принят, ждём код с почты и 2FA). В памяти: сервер один, вызов живёт
/// 10 минут, одноразовый, после 5 неверных кодов уничтожается. При перезапуске сервера вход просто начинают заново.
/// Токен вызова хранится только хэшем.
/// </summary>
public sealed class StaffLoginChallengeStore
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public sealed class Challenge
    {
        public required Guid Id { get; init; }
        public required Guid UserId { get; init; }
        public required string App { get; init; }
        public required bool RequiresTotp { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public int Attempts;
    }

    private readonly ConcurrentDictionary<string, Challenge> _byTokenHash = new(StringComparer.Ordinal);

    public (string Token, Challenge Challenge) Create(Guid userId, string app, bool requiresTotp)
    {
        Prune();
        var token = TotpService.NewLoginChallengeToken();
        var challenge = new Challenge
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            App = app,
            RequiresTotp = requiresTotp,
            ExpiresAt = DateTime.UtcNow + Lifetime,
        };
        _byTokenHash[TotpService.HashLoginChallengeToken(token)] = challenge;
        return (token, challenge);
    }

    public Challenge? Find(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var key = TotpService.HashLoginChallengeToken(token.Trim());
        if (!_byTokenHash.TryGetValue(key, out var c)) return null;
        if (c.ExpiresAt <= DateTime.UtcNow || Volatile.Read(ref c.Attempts) >= MaxAttempts)
        {
            _byTokenHash.TryRemove(key, out _);
            return null;
        }
        return c;
    }

    /// <summary>Неверный код. true — попытки кончились, вызов уничтожен.</summary>
    public bool RegisterFailure(string token)
    {
        var key = TotpService.HashLoginChallengeToken(token.Trim());
        if (!_byTokenHash.TryGetValue(key, out var c)) return true;
        if (Interlocked.Increment(ref c.Attempts) < MaxAttempts) return false;
        _byTokenHash.TryRemove(key, out _);
        return true;
    }

    /// <summary>Успешный вход: вызов больше не годится.</summary>
    public bool Consume(string token) => _byTokenHash.TryRemove(TotpService.HashLoginChallengeToken(token.Trim()), out _);

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, c) in _byTokenHash)
            if (c.ExpiresAt <= now)
                _byTokenHash.TryRemove(key, out _);
    }
}
