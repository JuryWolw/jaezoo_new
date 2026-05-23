using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models.Files;

public sealed class FileScanAllowList
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;

    public Guid? ApprovedByUserId { get; set; }

    public DateTime ApprovedAt { get; set; } = DateTime.UtcNow;
}
