using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public sealed class UserRole
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public GlobalRole Role { get; set; } = GlobalRole.User;
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public Guid? GrantedByUserId { get; set; }

    [MaxLength(256)]
    public string? Reason { get; set; }

    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }

    [MaxLength(256)]
    public string? RevokeReason { get; set; }

    public User? User { get; set; }
}
