using System.ComponentModel.DataAnnotations;
using JaeZoo.Server.Models;

namespace JaeZoo.Server.Models.Security;

public sealed class E2eeDeviceApprovalRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    [MaxLength(64)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Fingerprint { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? DeviceName { get; set; }

    [MaxLength(64)]
    public string? Platform { get; set; }

    [MaxLength(32)]
    public string? ClientVersion { get; set; }

    [MaxLength(64)]
    public string? LastIpAddress { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = "Pending";

    [MaxLength(64)]
    public string? ApprovedByDeviceId { get; set; }

    [MaxLength(256)]
    public string? Reason { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);
    public DateTime? ApprovedAt { get; set; }
    public DateTime? RejectedAt { get; set; }

    public User? User { get; set; }
}
