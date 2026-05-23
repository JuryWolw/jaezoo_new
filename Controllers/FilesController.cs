using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Files;
using JaeZoo.Server.Services.Files;
using JaeZoo.Server.Services.Storage;
using JaeZoo.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    IObjectStorage storage,
    FileInspectionService inspection,
    FileBucketRouter bucketRouter
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
        cfg.GetValue<long?>("Files:MaxUploadBytes") ?? (2L * 1024 * 1024 * 1024);

    private string StoragePath =>
        (cfg.GetValue<string>("Files:StoragePath") ?? "data/uploads").Trim();

    private string TempRoot =>
        Path.Combine(Path.GetTempPath(), "jaezoo", "uploads");

    private static bool IsImage(string? ct) =>
        !string.IsNullOrWhiteSpace(ct) && ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideo(string? ct) =>
        !string.IsNullOrWhiteSpace(ct) && ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private string GetAbsoluteStorageRoot()
    {
        return Path.IsPathRooted(StoragePath)
            ? StoragePath
            : Path.Combine(env.ContentRootPath, StoragePath);
    }

    private static string GetBucket(ChatFile file) =>
        string.IsNullOrWhiteSpace(file.Bucket) ? "jaezoo-files" : file.Bucket;

    private static string GetObjectKey(ChatFile file) =>
        string.IsNullOrWhiteSpace(file.ObjectKey) ? file.StoredPath : file.ObjectKey;

    private string BuildFileUrl(ChatFile file) => $"/api/files/{file.Id}/raw";

    private async Task<bool> CanAccessFileAsync(Guid me, Guid fileId, CancellationToken ct)
    {
        var f = await db.ChatFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId, ct);
        if (f is null) return false;
        if (f.BlockedAt.HasValue || f.DeletedAt.HasValue) return false;

        if (!f.IsAttached)
            return f.UploaderId == me;

        var canAccessDirect = await (
            from a in db.DirectMessageAttachments.AsNoTracking()
            join m in db.DirectMessages.AsNoTracking() on a.MessageId equals m.Id
            join d in db.DirectDialogs.AsNoTracking() on m.DialogId equals d.Id
            where a.FileId == fileId && (d.User1Id == me || d.User2Id == me)
            select a.Id
        ).AnyAsync(ct);

        if (canAccessDirect)
            return true;

        return await (
            from a in db.GroupMessageAttachments.AsNoTracking()
            join m in db.GroupMessages.AsNoTracking() on a.MessageId equals m.Id
            join gm in db.GroupChatMembers.AsNoTracking() on m.GroupChatId equals gm.GroupChatId
            where a.FileId == fileId && gm.UserId == me
            select a.Id
        ).AnyAsync(ct);
    }

    [HttpPost("upload")]
    [EnableRateLimiting("file-upload")]
    [RequireVerifiedEmail]
    [RequireRiskCaptcha("file-upload", 5, 60)]
    [RequestSizeLimit(long.MaxValue)]
    public async Task<ActionResult<FileUploadResponse>> Upload(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Файл не найден или пустой." });

        if (file.Length > MaxUploadBytes)
            return BadRequest(new { error = $"Слишком большой файл. Лимит: {MaxUploadBytes} bytes." });

        string? tempPath = null;
        try
        {
            var copied = await inspection.CopyToTempAndInspectAsync(file, TempRoot, ct);
            tempPath = copied.TempPath;
            var meta = copied.Result;
            var now = DateTime.UtcNow;
            var fileId = Guid.NewGuid();
            var bucket = bucketRouter.GetBucket(meta.Kind);
            var objectKey = FileBucketRouter.BuildObjectKey(meta.Kind, now, fileId, meta.Extension);

            await using (var input = System.IO.File.OpenRead(tempPath))
            {
                await storage.PutAsync(bucket, objectKey, input, meta.DetectedContentType, ct);
            }

            var entity = new ChatFile
            {
                Id = fileId,
                UploaderId = MeId,
                OriginalFileName = meta.SafeFileName,
                SafeFileName = meta.SafeFileName,
                ContentType = meta.DetectedContentType,
                DetectedContentType = meta.DetectedContentType,
                SizeBytes = file.Length,
                Bucket = bucket,
                ObjectKey = objectKey,
                StoredPath = objectKey,
                Sha256 = meta.Sha256Hex,
                Kind = meta.Kind,
                ScanStatus = meta.ScanStatus,
                IsPotentiallyDangerous = meta.IsPotentiallyDangerous,
                RiskNote = meta.RiskNote,
                CreatedAt = now,
                IsAttached = false
            };

            db.ChatFiles.Add(entity);
            await db.SaveChangesAsync(ct);

            log.LogInformation(
                "File uploaded. UserId={UserId} FileId={FileId} Kind={Kind} Bucket={Bucket} Key={Key} Size={Size} Sha256={Sha256} ContentType={ContentType} Dangerous={Dangerous}",
                MeId, entity.Id, entity.Kind, entity.Bucket, entity.ObjectKey, entity.SizeBytes, entity.Sha256, entity.ContentType, entity.IsPotentiallyDangerous);

            var url = BuildFileUrl(entity);
            return Ok(new FileUploadResponse(
                entity.Id,
                entity.OriginalFileName,
                entity.ContentType,
                entity.SizeBytes,
                url,
                meta.IsImage,
                meta.IsVideo
            ));
        }
        catch (AmazonS3Exception s3ex)
        {
            log.LogError(s3ex,
                "Object storage upload failed: user={UserId}, file={FileName}, status={Status}, code={Code}, requestId={ReqId}",
                MeId,
                file.FileName,
                s3ex.StatusCode,
                s3ex.ErrorCode,
                s3ex.RequestId);

            return StatusCode(502, new
            {
                error = "Object storage upload failed",
                status = s3ex.StatusCode.ToString(),
                code = s3ex.ErrorCode,
                requestId = s3ex.RequestId,
                message = s3ex.Message
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Upload failed: user={UserId}, file={FileName}", MeId, file.FileName);
            return Problem("Upload failed.", statusCode: 500);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                try
                {
                    if (System.IO.File.Exists(tempPath))
                        System.IO.File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Failed to delete temp upload file {TempPath}", tempPath);
                }
            }
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

        var publicUrl = BuildFileUrl(file);
        return Redirect(publicUrl);
    }

    [HttpGet("{id:guid}/raw")]
    public async Task<IActionResult> GetRaw(Guid id, CancellationToken ct = default)
    {
        var me = MeId;

        var file = await db.ChatFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound();

        var can = await CanAccessFileAsync(me, id, ct);
        if (!can) return Forbid();

        var bucket = GetBucket(file);
        var objectKey = GetObjectKey(file);

        try
        {
            var (stream, ctType, _) = await storage.GetAsync(bucket, objectKey, ct);
            Response.Headers.CacheControl = "private,max-age=3600";
            Response.Headers["X-JaeZoo-File-Kind"] = file.Kind.ToString();
            Response.Headers["X-JaeZoo-File-Sha256"] = file.Sha256 ?? string.Empty;
            return File(stream, ctType ?? file.ContentType ?? "application/octet-stream", enableRangeProcessing: true);
        }
        catch (AmazonS3Exception s3ex) when (s3ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            log.LogWarning("Object missing in object storage: id={FileId}, bucket={Bucket}, key={Key}", id, bucket, objectKey);
        }
        catch (AmazonS3Exception s3ex)
        {
            log.LogError(s3ex,
                "Storage get failed: id={FileId}, bucket={Bucket}, key={Key}, status={Status}, code={Code}, requestId={ReqId}",
                id, bucket, objectKey, s3ex.StatusCode, s3ex.ErrorCode, s3ex.RequestId);

            return StatusCode(502, new
            {
                error = "Object storage error",
                status = s3ex.StatusCode.ToString(),
                code = s3ex.ErrorCode,
                bucket,
                key = objectKey,
                requestId = s3ex.RequestId,
                message = s3ex.Message
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Storage get failed: id={FileId}, bucket={Bucket}, key={Key}", id, bucket, objectKey);
        }

        var root = GetAbsoluteStorageRoot();
        var legacyStoredPath = string.IsNullOrWhiteSpace(file.StoredPath) ? objectKey : file.StoredPath;
        var absPath = Path.Combine(root, legacyStoredPath.Replace('/', Path.DirectorySeparatorChar));

        if (!System.IO.File.Exists(absPath))
        {
            var bucketPath = Path.Combine(root, bucket, objectKey.Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(bucketPath))
            {
                absPath = bucketPath;
            }
            else
            {
                var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
                var legacyPath = Path.Combine(webRoot, "uploads", legacyStoredPath.Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(legacyPath))
                    absPath = legacyPath;
                else
                    return NotFound(new { error = "File is missing in object storage." });
            }
        }

        var lastWriteUtc = System.IO.File.GetLastWriteTimeUtc(absPath);
        Response.Headers["Last-Modified"] = lastWriteUtc.ToString("R");

        return new PhysicalFileResult(absPath, file.ContentType ?? "application/octet-stream")
        {
            EnableRangeProcessing = true
        };
    }
}
