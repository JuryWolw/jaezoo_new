using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class ChatFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid UploaderId { get; set; }

    [MaxLength(256)]
    public string OriginalFileName { get; set; } = string.Empty;

    [MaxLength(128)]
    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    // Относительный путь от корня StoragePath (например: "2026/01/<guid>.png")
    [MaxLength(512)]
    public string StoredPath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // true когда файл уже привязан к сообщению (чтобы не позволять повторно цеплять один и тот же upload)
    public bool IsAttached { get; set; } = false;

    public DateTime? AttachedAt { get; set; }
}
