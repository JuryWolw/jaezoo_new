using System;
using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public sealed class UserAvatar
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    [MaxLength(128)]
    public string Bucket { get; set; } = string.Empty;

    [MaxLength(512)]
    public string ObjectKey { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(128)]
    public string ContentType { get; set; } = "image/png";

    public long SizeBytes { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    public User? User { get; set; }
}
