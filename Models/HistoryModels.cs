namespace JaeZoo.Server.Models;

/// <summary>
/// The account's history vault (one per user). Both blobs are sealed on the devices: the keyring under a key
/// derived from the recovery key, the password wrap under Argon2id(cloud password). The server cannot open either.
/// </summary>
public sealed class HistoryVault
{
    public Guid UserId { get; set; }

    /// <summary>Optimistic concurrency: every change increments it; a change based on an older version is refused.</summary>
    public long Version { get; set; }

    public byte[] Keyring { get; set; } = Array.Empty<byte>();
    public byte[]? PasswordWrap { get; set; }
    public Guid UpdatedByDeviceId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One archived message of one user (sealed with the user's history key). Stored only for the message's current
/// version (<see cref="CiphertextHash"/> = SHA-256 of its MLS ciphertext); dropped when the message is deleted.
/// </summary>
public sealed class HistoryRecord
{
    public Guid UserId { get; set; }
    public Guid MessageId { get; set; }
    public string CiphertextHash { get; set; } = string.Empty;
    public byte[] Blob { get; set; } = Array.Empty<byte>();

    /// <summary>Paging position (microsecond precision, distinct within a batch).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum HistoryAccessStatus
{
    Pending = 0,
    Approved = 1,
    Declined = 2,
}

/// <summary>A new device asks the account's other devices for the history key (one-time MLS KeyPackage).</summary>
public sealed class HistoryAccessRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public byte[] KeyPackage { get; set; } = Array.Empty<byte>();
    public HistoryAccessStatus Status { get; set; }
    public Guid? ResolvedByDeviceId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}

/// <summary>The history key for one device: MLS Welcome to a one-shot group + one message. Opaque to the server.</summary>
public sealed class HistoryGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid TargetDeviceId { get; set; }
    public Guid FromDeviceId { get; set; }
    public byte[] Welcome { get; set; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
