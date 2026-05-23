using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services.Storage;
using Microsoft.AspNetCore.SignalR;

namespace JaeZoo.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class UsersController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ILogger<UsersController> _log;
        private readonly IWebHostEnvironment _env;
        private readonly IHubContext<ChatHub> _hub;
        private readonly IObjectStorage _storage;
        private readonly IConfiguration _cfg;

        public UsersController(AppDbContext db, ILogger<UsersController> log, IWebHostEnvironment env, IHubContext<ChatHub> hub, IObjectStorage storage, IConfiguration cfg)
        {
            _db = db;
            _log = log;
            _env = env;
            _hub = hub;
            _storage = storage;
            _cfg = cfg;
        }

        private Guid MeId
        {
            get
            {
                var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException("No NameIdentifier claim.");
                return Guid.Parse(id);
            }
        }

        private string AvatarBucket => _cfg["ObjectStorage:Buckets:Avatars"] ?? "jaezoo-avatars";

        private async Task<List<string>> GetProfileUpdateTargetsAsync(Guid userId, CancellationToken ct)
        {
            var friendIds = await _db.Friendships
                .AsNoTracking()
                .Where(f => f.Status == FriendshipStatus.Accepted &&
                            (f.RequesterId == userId || f.AddresseeId == userId))
                .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId)
                .Distinct()
                .ToListAsync(ct);

            return friendIds.Select(x => x.ToString())
                .Append(userId.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task NotifyAvatarChangedAsync(Guid userId, string? avatarUrl, CancellationToken ct)
        {
            try
            {
                var targets = await GetProfileUpdateTargetsAsync(userId, ct);
                await _hub.Clients.Users(targets)
                    .SendAsync("UserAvatarChanged", new { userId = userId.ToString(), avatarUrl }, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to notify friends about avatar change for user {UserId}.", userId);
            }
        }

        private async Task NotifyProfileChangedAsync(User user, CancellationToken ct)
        {
            try
            {
                var targets = await GetProfileUpdateTargetsAsync(user.Id, ct);
                await _hub.Clients.Users(targets).SendAsync("UserProfileChanged", new
                {
                    userId = user.Id.ToString(),
                    displayName = UserIdentityService.GetPublicName(user),
                    avatarUrl = UserIdentityService.GetAvatarUrl(user),
                    profileBannerUrl = user.ProfileBannerUrl,
                    profileTextTheme = string.IsNullOrWhiteSpace(user.ProfileTextTheme) ? "Light" : user.ProfileTextTheme
                }, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to notify profile changed for user {UserId}.", user.Id);
            }
        }

        // ===== Поиск =====
        [HttpGet("search")]
        public async Task<ActionResult<IEnumerable<UserSearchDto>>> Search([FromQuery] string q, CancellationToken ct)
        {
            var meId = MeId;
            var query = (q ?? string.Empty).Trim();

            if (query.Length < 2)
                return Ok(Array.Empty<UserSearchDto>());

            var qLower = query.ToLowerInvariant();
            var qUpper = query.ToUpperInvariant();

            var prov = _db.Database.ProviderName ?? string.Empty;
            IQueryable<User> baseQuery = _db.Users.Where(u => u.Id != meId && !u.IsDisabled);

            if (prov.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                baseQuery = baseQuery.Where(u =>
                    EF.Functions.ILike(u.DisplayName!, $"%{query}%") ||
                    EF.Functions.ILike(u.PublicId!, $"%{query}%"));
            }
            else
            {
                baseQuery = baseQuery.Where(u =>
                    (u.DisplayName ?? "").ToLower().Contains(qLower) ||
                    (u.PublicId ?? "").ToUpper().Contains(qUpper));
            }

            var users = await baseQuery
                .AsNoTracking()
                .OrderBy(u => u.DisplayName)
                .Take(25)
                .ToListAsync(ct);

            var res = users
                .Select(u => new UserSearchDto(
                    u.Id,
                    UserIdentityService.GetPublicName(u),
                    string.Empty,
                    UserIdentityService.GetAvatarUrl(u),
                    UserIdentityService.GetPublicName(u),
                    u.PublicId))
                .ToList();

            return Ok(res);
        }

        [HttpGet("me")]
        public async Task<ActionResult<UserProfileDto>> Me(CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);
            return Ok(ToProfileDto(me));
        }

        [HttpGet("{id:guid}")]
        [AllowAnonymous]
        public async Task<ActionResult<PublicUserDto>> GetPublic(Guid id, CancellationToken ct)
        {
            var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u == null) return NotFound();

            return Ok(ToPublicDto(u));
        }

        [HttpPut("profile")]
        [RequireVerifiedEmail]
        public async Task<ActionResult<UserProfileDto>> UpdateProfile([FromBody] UpdateProfileRequest body, CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);

            if (body.DisplayName != null)
            {
                me.DisplayName = body.DisplayName.Trim();
                if (me.DisplayName.Length == 0) me.DisplayName = UserIdentityService.CreateRandomDisplayName();
                if (me.DisplayName.Length > 64) return BadRequest("Никнейм не должен быть длиннее 64 символов.");
                me.UpdatedAt = DateTime.UtcNow;
            }
            if (body.About != null)
            {
                me.About = body.About.Trim();
                if (me.About.Length == 0) me.About = null;
                if (me.About?.Length > 256) return BadRequest("Описание не должно быть длиннее 256 символов.");
                me.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
            await NotifyProfileChangedAsync(me, ct);
            return Ok(ToProfileDto(me));
        }

        [HttpPut("status")]
        [RequireVerifiedEmail]
        public async Task<ActionResult<UserProfileDto>> UpdateStatus([FromBody] UpdateStatusRequest body, CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);

            me.Status = body.Status;
            me.CustomStatus = string.IsNullOrWhiteSpace(body.CustomStatus) ? null : body.CustomStatus.Trim();
            if (me.CustomStatus?.Length > 64) return BadRequest("Статус не должен быть длиннее 64 символов.");
            me.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
            await NotifyProfileChangedAsync(me, ct);
            return Ok(ToProfileDto(me));
        }

        [HttpPut("avatar/url")]
        [RequireVerifiedEmail]
        public async Task<ActionResult<UserProfileDto>> SetAvatarUrl([FromBody] SetAvatarUrlRequest body, CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);
            me.AvatarUrl = string.IsNullOrWhiteSpace(body.AvatarUrl) ? null : body.AvatarUrl.Trim();
            me.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await NotifyAvatarChangedAsync(me.Id, string.IsNullOrWhiteSpace(me.AvatarUrl) ? $"/avatars/{me.Id}" : me.AvatarUrl, ct);
            await NotifyProfileChangedAsync(me, ct);
            return Ok(ToProfileDto(me));
        }

        [Authorize]
        [RequestSizeLimit(12 * 1024 * 1024)]
        [HttpPost("avatar/upload")]
        [RequireVerifiedEmail]
        public async Task<ActionResult<UserProfileDto>> UploadAvatar(IFormFile file, CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);
            var created = await UploadProfileImageAsync(file, $"users/{me.Id:D}/avatars", ct);

            await _db.UserAvatars
                .Where(a => a.UserId == me.Id && a.DeletedAt == null && a.IsCurrent)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.IsCurrent, false), ct);

            var entity = new UserAvatar
            {
                Id = Guid.NewGuid(),
                UserId = me.Id,
                Bucket = created.Bucket,
                ObjectKey = created.ObjectKey,
                Url = created.Url,
                ContentType = created.ContentType,
                SizeBytes = created.SizeBytes,
                IsCurrent = true,
                CreatedAt = DateTime.UtcNow
            };

            _db.UserAvatars.Add(entity);
            me.AvatarUrl = created.Url;
            me.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await NotifyAvatarChangedAsync(me.Id, me.AvatarUrl, ct);
            await NotifyProfileChangedAsync(me, ct);
            return Ok(ToProfileDto(me));
        }

        [HttpGet("avatar-gallery")]
        [RequireVerifiedEmail]
        public async Task<ActionResult<IReadOnlyList<UserAvatarDto>>> GetAvatarGallery(CancellationToken ct)
        {
            var uid = MeId;
            var avatars = await _db.UserAvatars.AsNoTracking()
                .Where(a => a.UserId == uid && a.DeletedAt == null)
                .OrderByDescending(a => a.IsCurrent)
                .ThenByDescending(a => a.CreatedAt)
                .Select(a => new UserAvatarDto(a.Id, a.Url, a.IsCurrent, a.CreatedAt))
                .ToListAsync(ct);
            return Ok(avatars);
        }

        [HttpPut("avatar-gallery/{avatarId:guid}/main")]
        [RequireVerifiedEmail]
        public async Task<ActionResult<UserProfileDto>> SetMainAvatar(Guid avatarId, CancellationToken ct)
        {
            var uid = MeId;
            var avatar = await _db.UserAvatars.FirstOrDefaultAsync(a => a.Id == avatarId && a.UserId == uid && a.DeletedAt == null, ct);
            if (avatar == null) return NotFound("Аватар не найден.");

            await _db.UserAvatars
                .Where(a => a.UserId == uid && a.DeletedAt == null && a.IsCurrent)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.IsCurrent, false), ct);

            avatar.IsCurrent = true;
            var me = await _db.Users.FirstAsync(u => u.Id == uid, ct);
            me.AvatarUrl = avatar.Url;
            me.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await NotifyAvatarChangedAsync(uid, me.AvatarUrl, ct);
            await NotifyProfileChangedAsync(me, ct);
            return Ok(ToProfileDto(me));
        }

        [HttpDelete("avatar-gallery/{avatarId:guid}")]
        [RequireVerifiedEmail]
        public async Task<ActionResult<UserProfileDto>> DeleteAvatar(Guid avatarId, CancellationToken ct)
        {
            var uid = MeId;
            var avatar = await _db.UserAvatars.FirstOrDefaultAsync(a => a.Id == avatarId && a.UserId == uid && a.DeletedAt == null, ct);
            if (avatar == null) return NotFound("Аватар не найден.");

            var wasCurrent = avatar.IsCurrent;
            avatar.DeletedAt = DateTime.UtcNow;
            avatar.IsCurrent = false;

            try { await _storage.DeleteAsync(avatar.Bucket, avatar.ObjectKey, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to delete avatar object {Bucket}/{Key}", avatar.Bucket, avatar.ObjectKey); }

            var me = await _db.Users.FirstAsync(u => u.Id == uid, ct);
            if (wasCurrent)
            {
                var next = await _db.UserAvatars
                    .Where(a => a.UserId == uid && a.DeletedAt == null && a.Id != avatar.Id)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefaultAsync(ct);

                if (next != null)
                {
                    next.IsCurrent = true;
                    me.AvatarUrl = next.Url;
                }
                else
                {
                    me.AvatarUrl = null;
                }
                me.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
            await NotifyAvatarChangedAsync(uid, me.AvatarUrl ?? $"/avatars/{uid}", ct);
            await NotifyProfileChangedAsync(me, ct);
            return Ok(ToProfileDto(me));
        }

        [Authorize]
        [RequestSizeLimit(16 * 1024 * 1024)]
        [HttpPost("banner/upload")]
        [RequireVerifiedEmail]
        public async Task<ActionResult<UserProfileDto>> UploadBanner(IFormFile file, CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);
            var previousUrl = me.ProfileBannerUrl;
            var created = await UploadProfileImageAsync(file, $"users/{me.Id:D}/banners", ct);

            if (!string.IsNullOrWhiteSpace(previousUrl))
            {
                // best-effort cleanup only for objects from our bucket/key shape
                TryDeleteByPublicUrl(previousUrl, ct);
            }

            me.ProfileBannerUrl = created.Url;
            me.ProfileTextTheme = "Light";
            me.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await NotifyProfileChangedAsync(me, ct);
            return Ok(ToProfileDto(me));
        }

        [HttpDelete("banner")]
        [RequireVerifiedEmail]
        public async Task<ActionResult<UserProfileDto>> DeleteBanner(CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);
            var previousUrl = me.ProfileBannerUrl;
            me.ProfileBannerUrl = null;
            me.ProfileTextTheme = "Light";
            me.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            if (!string.IsNullOrWhiteSpace(previousUrl))
                TryDeleteByPublicUrl(previousUrl, ct);
            await NotifyProfileChangedAsync(me, ct);
            return Ok(ToProfileDto(me));
        }

        private void TryDeleteByPublicUrl(string url, CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var marker = $"/{AvatarBucket}/";
                    var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) return;
                    var key = Uri.UnescapeDataString(url[(idx + marker.Length)..]);
                    var q = key.IndexOf('?');
                    if (q >= 0) key = key[..q];
                    if (!string.IsNullOrWhiteSpace(key))
                        await _storage.DeleteAsync(AvatarBucket, key, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete old profile media object by url.");
                }
            }, CancellationToken.None);
        }

        private async Task<(string Bucket, string ObjectKey, string Url, string ContentType, long SizeBytes)> UploadProfileImageAsync(IFormFile file, string prefix, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
                throw new BadHttpRequestException("Файл не найден.");

            if (file.Length > 16 * 1024 * 1024)
                throw new BadHttpRequestException("Изображение не должно быть больше 16 МБ.");

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var contentType = (file.ContentType ?? string.Empty).ToLowerInvariant();
            if (ext is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
                throw new BadHttpRequestException("Поддерживаются только PNG/JPEG/WEBP.");
            if (contentType is not "image/png" and not "image/jpeg" and not "image/webp")
                contentType = ext switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };

            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            if (!LooksLikeImage(bytes, contentType))
                throw new BadHttpRequestException("Файл не похож на корректное изображение.");

            var safeExt = contentType switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => ".jpg"
            };
            var key = $"{prefix.Trim('/')}/{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{safeExt}";
            await using var upload = new MemoryStream(bytes, writable: false);
            await _storage.PutAsync(AvatarBucket, key, upload, contentType, ct);
            var url = _storage.GetPublicUrl(AvatarBucket, key);
            return (AvatarBucket, key, url, contentType, bytes.LongLength);
        }

        private static bool LooksLikeImage(byte[] bytes, string contentType)
        {
            if (bytes.Length < 12) return false;
            if (contentType == "image/png") return bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
            if (contentType == "image/jpeg") return bytes[0] == 0xFF && bytes[1] == 0xD8;
            if (contentType == "image/webp") return bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50;
            return false;
        }

        [AllowAnonymous]
        [HttpGet("/avatars/{id:guid}")]
        public async Task<IActionResult> GetAvatar(Guid id, CancellationToken ct)
        {
            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
            if (!string.IsNullOrWhiteSpace(user?.AvatarUrl) && user.AvatarUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return Redirect(user.AvatarUrl);

            var avatar = await _db.Avatars.AsNoTracking()
                .Where(a => a.UserId == id)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (avatar is null || avatar.Data.Length == 0)
            {
                var path = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "avatars", "default.png");
                if (System.IO.File.Exists(path))
                    return PhysicalFile(path, "image/png");
                return NotFound();
            }

            var etag = $"W/\"{avatar.Data.Length}-{avatar.CreatedAt.ToUniversalTime():yyyyMMddHHmmss}\"";
            var ifNone = Request.Headers["If-None-Match"].ToString();
            if (!string.IsNullOrEmpty(ifNone) && string.Equals(ifNone, etag, StringComparison.Ordinal))
                return StatusCode(StatusCodes.Status304NotModified);

            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "public,max-age=3600";

            return File(avatar.Data, avatar.ContentType ?? "image/png");
        }

        private static UserProfileDto ToProfileDto(User u) =>
            new UserProfileDto(
                u.Id,
                UserIdentityService.GetLogin(u),
                u.Email,
                UserIdentityService.GetPublicName(u),
                UserIdentityService.GetAvatarUrl(u),
                u.About,
                u.Status, u.CustomStatus,
                u.CreatedAt, u.LastSeen,
                u.PublicId,
                u.EmailConfirmed,
                u.EmailVerifiedAt,
                u.ProfileBannerUrl
            );

        private static PublicUserDto ToPublicDto(User u) =>
            new PublicUserDto(
                u.Id,
                u.PublicId,
                UserIdentityService.GetPublicName(u),
                UserIdentityService.GetAvatarUrl(u),
                u.Status, u.CustomStatus, u.ShowOnline ? u.LastSeen : null,
                u.ProfileBannerUrl
            );
    }
}
