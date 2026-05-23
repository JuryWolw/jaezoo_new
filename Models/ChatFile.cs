using System.ComponentModel.DataAnnotations;
using JaeZoo.Server.Models.Files;

namespace JaeZoo.Server.Models;

public class ChatFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid UploaderId { get; set; }

    [MaxLength(256)]
    public string OriginalFileName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string SafeFileName { get; set; } = string.Empty;

    [MaxLength(128)]
    public string ContentType { get; set; } = "application/octet-stream";

    [MaxLength(128)]
    public string DetectedContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    // Legacy name kept for compatibility with old code. It now stores object key.
    [MaxLength(512)]
    public string StoredPath { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Bucket { get; set; } = "jaezoo-files";

    [MaxLength(512)]
    public string ObjectKey { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public StoredFileKind Kind { get; set; } = StoredFileKind.File;

    public FileScanStatus ScanStatus { get; set; } = FileScanStatus.NotScanned;

    public bool IsPotentiallyDangerous { get; set; }

    [MaxLength(512)]
    public string? RiskNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsAttached { get; set; } = false;

    public DateTime? AttachedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime? BlockedAt { get; set; }
}
