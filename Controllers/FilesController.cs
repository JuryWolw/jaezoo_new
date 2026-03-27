using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FilesController(
    AppDbContext db,
    IWebHostEnvironment env,
    IConfiguration cfg,
    ILogger<FilesController> log,
    IObjectStorage storage
) : ControllerBase
{
    private Guid MeId
    {
        get
        {
            var s = User.FindFirst("sub")?.Value
                    ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst("uid")?.Value;

            if (!Guid.TryParse(s, out var id))
                throw new UnauthorizedAccessException("No user id claim.");

            return id;
        }
    }

    private long MaxUploadBytes =>
        cfg.GetValue<long?>("Files:MaxUploadBytes") ?? (50L * 1024 * 1024);

    private string StoragePath =>
        (cfg.GetValue<string>("Files:StoragePath") ?? "data/uploads").Trim();

    private string[] AllowedTypes =>
        cfg.GetSection("Files:AllowedContentTypes").Get<string[]>() ?? Array.Empty<string>();

    private static bool IsImage(string? ct) =>
        !string.IsNullOrWhiteSpace(ct) && ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideo(string? ct) =>
        !string.IsNullOrWhiteSpace(ct) && ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFileName(string? name)
    {
        name ??= "file";
        name = name.Trim();
        if (name.Length == 0) name = "file";
        if (name.Length > 200) name = name[..200];
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private string GetAbsoluteStorageRoot()
    {
        return Path.IsPathRooted(StoragePath)
            ? StoragePath
            : Path.Combine(env.ContentRootPath, StoragePath);
    }

    private string BuildFileUrl(ChatFile file) => storage.GetPublicUrl(file.StoredPath);

    private async Task<bool> CanAccessFileAsync(Guid me, Guid fileId, CancellationToken ct)
    {
        var f = await db.ChatFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId, ct);
        if (f is null) return false;

        if (!f.IsAttached)
            return f.UploaderId == me;

        return await (
            from a in db.DirectMessageAttachments.AsNoTracking()
            join m in db.DirectMessages.AsNoTracking() on a.MessageId equals m.Id
            join d in db.DirectDialogs.AsNoTracking() on m.DialogId equals d.Id
            where a.FileId == fileId && (d.User1Id == me || d.User2Id == me)
            select a.Id
        ).AnyAsync(ct);
    }

    [HttpPost("upload")]
    [RequestSizeLimit(long.MaxValue)]
    public async Task<ActionResult<FileUploadResponse>> Upload(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Файл не найден или пустой." });

        if (file.Length > MaxUploadBytes)
            return BadRequest(new { error = $"Слишком большой файл. Лимит: {MaxUploadBytes} bytes." });

        var contentType = (file.ContentType ?? "application/octet-stream").Trim();
        if (AllowedTypes.Length > 0 && !AllowedTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = "Этот тип файла запрещён настройками сервера." });

        var safeName = SanitizeFileName(file.FileName);
        var ext = Path.GetExtension(safeName);
        if (ext.Length > 12) ext = ext[..12];

        var now = DateTime.UtcNow;
        var relDir = $"{now:yyyy}/{now:MM}";
        var storedName = $"{Guid.NewGuid():N}{ext}";
        var objectKey = $"{relDir}/{storedName}";

        try
        {
            await using var input = file.OpenReadStream();
            await storage.PutAsync(objectKey, input, contentType, ct);

            var entity = new ChatFile
            {
                UploaderId = MeId,
                OriginalFileName = safeName,
                ContentType = contentType,
                SizeBytes = file.Length,
                StoredPath = objectKey,
                CreatedAt = DateTime.UtcNow,
                IsAttached = false
            };

            db.ChatFiles.Add(entity);
            await db.SaveChangesAsync(ct);

            var url = BuildFileUrl(entity);
            return Ok(new FileUploadResponse(
                entity.Id,
                entity.OriginalFileName,
                entity.ContentType,
                entity.SizeBytes,
                url,
                IsImage(entity.ContentType),
                IsVideo(entity.ContentType)
            ));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Upload failed: user={UserId}, file={FileName}", MeId, safeName);
            return Problem("Upload failed.", statusCode: 500);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
    {
        var me = MeId;

        var file = await db.ChatFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound();

        var can = await CanAccessFileAsync(me, id, ct);
        if (!can) return Forbid();

        // Главный быстрый путь: отдаём redirect на CDN.
        var publicUrl = BuildFileUrl(file);
        return Redirect(publicUrl);
    }

    // optional fallback endpoint if CDN/origin temporarily unavailable during migration
    [HttpGet("{id:guid}/raw")]
    public async Task<IActionResult> GetRaw(Guid id, CancellationToken ct = default)
    {
        var me = MeId;

        var file = await db.ChatFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound();

        var can = await CanAccessFileAsync(me, id, ct);
        if (!can) return Forbid();

        try
        {
            var (stream, ctType, _) = await storage.GetAsync(file.StoredPath, ct);
            Response.Headers.CacheControl = "private,max-age=3600";
            return File(stream, ctType ?? file.ContentType ?? "application/octet-stream", enableRangeProcessing: true);
        }
        catch (AmazonS3Exception s3ex) when (s3ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            log.LogWarning("Object missing in object storage: id={FileId}, key={Key}", id, file.StoredPath);
        }
        catch (AmazonS3Exception s3ex)
        {
            log.LogError(s3ex,
                "Storage get failed: id={FileId}, key={Key}, status={Status}, code={Code}, requestId={ReqId}",
                id, file.StoredPath, s3ex.StatusCode, s3ex.ErrorCode, s3ex.RequestId);

            return StatusCode(502, new
            {
                error = "Object storage error",
                status = s3ex.StatusCode.ToString(),
                code = s3ex.ErrorCode,
                key = file.StoredPath
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Storage get failed: id={FileId}, key={Key}", id, file.StoredPath);
        }

        // Legacy local fallback
        var root = GetAbsoluteStorageRoot();
        var absPath = Path.Combine(root, file.StoredPath.Replace('/', Path.DirectorySeparatorChar));

        if (!System.IO.File.Exists(absPath))
        {
            var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
            var legacyPath = Path.Combine(webRoot, "uploads", file.StoredPath.Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(legacyPath))
                absPath = legacyPath;
            else
                return NotFound(new { error = "File is missing in object storage." });
        }

        var lastWriteUtc = System.IO.File.GetLastWriteTimeUtc(absPath);
        Response.Headers["Last-Modified"] = lastWriteUtc.ToString("R");

        return new PhysicalFileResult(absPath, file.ContentType ?? "application/octet-stream")
        {
            EnableRangeProcessing = true
        };
    }
}