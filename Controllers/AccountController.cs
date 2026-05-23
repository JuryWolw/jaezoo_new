using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Security;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers
{
    [ApiController]
    [Route("api/users/account")]
    [Authorize]
    public class AccountController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly SmartCaptchaService _captcha;
        private readonly IObjectStorage _storage;
        private readonly ILogger<AccountController> _log;
        private readonly IConfiguration _cfg;

        public AccountController(AppDbContext db, SmartCaptchaService captcha, IObjectStorage storage, ILogger<AccountController> log, IConfiguration cfg)
        {
            _db = db;
            _captcha = captcha;
            _storage = storage;
            _log = log;
            _cfg = cfg;
        }

        private async Task<IActionResult?> RequireCaptchaAsync(string? token, CancellationToken ct)
        {
            var result = await _captcha.ValidateAsync(token, HttpContext, ct);
            if (result.Success) return null;

            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "captcha_required",
                message = result.Message
            });
        }

        private Guid MeId
        {
            get
            {
                var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
                return Guid.TryParse(id, out var g) ? g : Guid.Empty;
            }
        }

        private string AvatarBucket => _cfg["ObjectStorage:Buckets:Avatars"] ?? "jaezoo-avatars";

        // PUT /api/users/account/username
        // Legacy route: фактически меняет приватный login. Публичный ник меняется через PUT /api/users/profile.
        [HttpPut("username")]
        [RequireVerifiedEmail]
        public async Task<IActionResult> ChangeUserName([FromBody] ChangeUserNameRequest body, CancellationToken ct)
        {
            if (body == null) return BadRequest(new { message = "Body is required." });
            var captchaError = await RequireCaptchaAsync(body.CaptchaToken, ct);
            if (captchaError != null) return captchaError;

            var current = (body.CurrentUserName ?? string.Empty).Trim();
            var next = (body.NewUserName ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(next))
                return BadRequest(new { message = "currentUserName and newUserName are required." });

            if (!UserIdentityService.IsValidLogin(next))
                return BadRequest(new { message = "Логин должен быть 3-32 символа: латиница, цифры, точка, дефис или подчёркивание." });

            var me = await _db.Users.FirstOrDefaultAsync(u => u.Id == MeId, ct);
            if (me == null) return Unauthorized();

            var currentLogin = UserIdentityService.GetLogin(me);
            if (!string.Equals(currentLogin, current, StringComparison.Ordinal))
                return NotFound(new { message = "Неверный текущий логин." });

            if (string.Equals(currentLogin, next, StringComparison.Ordinal))
                return Ok(new { message = "Логин не изменился." });

            var loginHash = IdentityDataProtector.HashLogin(next);
            var exists = await _db.Users.AnyAsync(
                u => u.Id != me.Id && u.LoginHash == loginHash, ct);
            if (exists) return Conflict(new { message = "Такой логин уже занят." });

            IdentityDataProtector.SetLogin(me, next);
            me.SecurityStamp = UserIdentityService.NewSecurityStamp();
            me.TokenVersion++;
            me.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        // PUT /api/users/account/email
        // Разрешено даже неподтверждённым пользователям: если человек ошибся в почте при регистрации,
        // он должен иметь возможность исправить её и подтвердить аккаунт.
        [HttpPut("email")]
        public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest body, CancellationToken ct)
        {
            if (body == null) return BadRequest(new { message = "Body is required." });
            var captchaError = await RequireCaptchaAsync(body.CaptchaToken, ct);
            if (captchaError != null) return captchaError;

            var current = (body.CurrentEmail ?? string.Empty).Trim();
            var next = (body.NewEmail ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(next))
                return BadRequest(new { message = "currentEmail and newEmail are required." });

            if (!new EmailAddressAttribute().IsValid(next))
                return UnprocessableEntity(new { message = "Некорректный адрес почты." });

            var me = await _db.Users.FirstOrDefaultAsync(u => u.Id == MeId, ct);
            if (me == null) return Unauthorized();

            var currentEmail = UserIdentityService.GetEmail(me);
            if (!string.Equals(currentEmail, current, StringComparison.OrdinalIgnoreCase))
                return NotFound(new { message = "Неверная текущая почта." });

            if (string.Equals(currentEmail, next, StringComparison.OrdinalIgnoreCase))
                return Ok(new { message = "Почта не изменилась." });

            var emailHash = IdentityDataProtector.HashEmail(next);
            var exists = await _db.Users.AnyAsync(
                u => u.Id != me.Id && u.EmailHash == emailHash, ct);
            if (exists) return Conflict(new { message = "Эта почта уже используется." });

            IdentityDataProtector.SetEmail(me, next);
            me.EmailConfirmed = false;
            me.EmailVerifiedAt = null;
            me.SecurityStamp = UserIdentityService.NewSecurityStamp();
            me.TokenVersion++;
            me.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        // PUT /api/users/account/password
        [HttpPut("password")]
        [RequireVerifiedEmail]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest body, CancellationToken ct)
        {
            if (body == null) return BadRequest(new { message = "Body is required." });
            var captchaError = await RequireCaptchaAsync(body.CaptchaToken, ct);
            if (captchaError != null) return captchaError;

            var current = body.CurrentPassword ?? string.Empty;
            var next = body.NewPassword ?? string.Empty;

            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(next))
                return BadRequest(new { message = "currentPassword and newPassword are required." });

            if (next.Length < 8)
                return BadRequest(new { message = "Новый пароль должен быть не короче 8 символов." });

            var me = await _db.Users.FirstOrDefaultAsync(u => u.Id == MeId, ct);
            if (me == null) return Unauthorized();

            var hash = me.PasswordHash ?? string.Empty;
            if (string.IsNullOrWhiteSpace(hash))
                return StatusCode(501, new { message = "Неизвестный формат хранения пароля (пустой PasswordHash)." });

            bool ok = false;

            if (hash.StartsWith("AQAAAA", StringComparison.Ordinal))
            {
                var ph = new PasswordHasher<User>();
                var res = ph.VerifyHashedPassword(me, hash, current);
                ok = res != PasswordVerificationResult.Failed;
                if (ok)
                    me.PasswordHash = ph.HashPassword(me, next);
            }
            else if (hash.StartsWith("$2", StringComparison.Ordinal))
            {
                ok = BCrypt.Net.BCrypt.Verify(current, hash);
                if (ok)
                    me.PasswordHash = BCrypt.Net.BCrypt.HashPassword(next);
            }
            else
            {
                return StatusCode(501, new { message = "Неизвестный формат пароля." });
            }

            if (!ok)
                return NotFound(new { message = "Неверный текущий пароль." });

            me.SecurityStamp = UserIdentityService.NewSecurityStamp();
            me.TokenVersion++;
            me.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        // POST /api/users/account/delete
        [HttpPost("delete")]
        public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountRequest body, CancellationToken ct)
        {
            if (MeId == Guid.Empty) return Unauthorized();
            var captchaError = await RequireCaptchaAsync(body?.CaptchaToken, ct);
            if (captchaError != null) return captchaError;

            var uid = MeId;
            var me = await _db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
            if (me == null) return NotFound(new { message = "Аккаунт уже удалён." });

            var storageObjects = new List<(string Bucket, string Key, string Reason)>();

            var avatars = await _db.UserAvatars.AsNoTracking()
                .Where(a => a.UserId == uid && !string.IsNullOrWhiteSpace(a.ObjectKey))
                .Select(a => new { a.Bucket, a.ObjectKey })
                .ToListAsync(ct);
            storageObjects.AddRange(avatars.Select(a => (string.IsNullOrWhiteSpace(a.Bucket) ? AvatarBucket : a.Bucket, a.ObjectKey, "avatar")));

            if (TryParseStorageUrl(me.ProfileBannerUrl, out var bannerBucket, out var bannerKey))
                storageObjects.Add((bannerBucket, bannerKey, "profile-banner"));

            var uploadedFiles = await _db.ChatFiles.AsNoTracking()
                .Where(f => f.UploaderId == uid && !string.IsNullOrWhiteSpace(f.ObjectKey))
                .Select(f => new { f.Bucket, f.ObjectKey, f.StoredPath })
                .ToListAsync(ct);
            storageObjects.AddRange(uploadedFiles.Select(f => (
                string.IsNullOrWhiteSpace(f.Bucket) ? "jaezoo-files" : f.Bucket,
                string.IsNullOrWhiteSpace(f.ObjectKey) ? f.StoredPath : f.ObjectKey,
                "chat-file")));

            foreach (var (bucket, key, reason) in storageObjects.Distinct())
            {
                if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(key)) continue;
                try
                {
                    await _storage.DeleteAsync(bucket, key, ct);
                    _log.LogInformation("Deleted account storage object. UserId={UserId} Bucket={Bucket} Key={Key} Reason={Reason}", uid, bucket, key, reason);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete account storage object. UserId={UserId} Bucket={Bucket} Key={Key} Reason={Reason}", uid, bucket, key, reason);
                }
            }

            var ownedGroupIds = await _db.GroupChats.AsNoTracking()
                .Where(g => g.OwnerId == uid)
                .Select(g => g.Id)
                .ToListAsync(ct);

            var directDialogIds = await _db.DirectDialogs.AsNoTracking()
                .Where(d => d.User1Id == uid || d.User2Id == uid)
                .Select(d => d.Id)
                .ToListAsync(ct);

            var directMessageIds = await _db.DirectMessages.AsNoTracking()
                .Where(m => m.SenderId == uid || directDialogIds.Contains(m.DialogId))
                .Select(m => m.Id)
                .ToListAsync(ct);

            var groupMessageIds = await _db.GroupMessages.AsNoTracking()
                .Where(m => m.SenderId == uid || ownedGroupIds.Contains(m.GroupChatId))
                .Select(m => m.Id)
                .ToListAsync(ct);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            await _db.DirectMessageAttachments.Where(a => directMessageIds.Contains(a.MessageId)).ExecuteDeleteAsync(ct);
            await _db.GroupMessageAttachments.Where(a => groupMessageIds.Contains(a.MessageId)).ExecuteDeleteAsync(ct);

            await _db.DirectMessages.Where(m => directMessageIds.Contains(m.Id)).ExecuteDeleteAsync(ct);
            await _db.GroupMessages.Where(m => groupMessageIds.Contains(m.Id)).ExecuteDeleteAsync(ct);
            await _db.DirectDialogs.Where(d => directDialogIds.Contains(d.Id)).ExecuteDeleteAsync(ct);

            await _db.GroupVoiceParticipants.Where(p => p.UserId == uid || ownedGroupIds.Contains(p.GroupChatId)).ExecuteDeleteAsync(ct);
            await _db.GroupVoiceSessions.Where(s => ownedGroupIds.Contains(s.GroupChatId)).ExecuteDeleteAsync(ct);
            await _db.GroupChatMembers.Where(m => m.UserId == uid || ownedGroupIds.Contains(m.GroupChatId)).ExecuteDeleteAsync(ct);
            await _db.GroupAvatars.Where(a => ownedGroupIds.Contains(a.GroupChatId)).ExecuteDeleteAsync(ct);
            await _db.GroupChats.Where(g => ownedGroupIds.Contains(g.Id)).ExecuteDeleteAsync(ct);

            await _db.Friendships.Where(f => f.RequesterId == uid || f.AddresseeId == uid).ExecuteDeleteAsync(ct);
            await _db.UserRoles.Where(r => r.UserId == uid).ExecuteDeleteAsync(ct);
            await _db.AdminAuditLogs.Where(a => a.ActorUserId == uid).ExecuteDeleteAsync(ct);
            await _db.UserSessions.Where(s => s.UserId == uid).ExecuteDeleteAsync(ct);
            await _db.EmailVerificationCodes.Where(c => c.UserId == uid).ExecuteDeleteAsync(ct);
            await _db.Avatars.Where(a => a.UserId == uid).ExecuteDeleteAsync(ct);
            await _db.UserAvatars.Where(a => a.UserId == uid).ExecuteDeleteAsync(ct);
            await _db.ChatFiles.Where(f => f.UploaderId == uid).ExecuteDeleteAsync(ct);
            await _db.Users.Where(u => u.Id == uid).ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);

            _log.LogWarning("User account deleted completely. UserId={UserId} PublicId={PublicId}", uid, me.PublicId);
            return Ok(new { message = "Аккаунт удалён." });
        }

        private bool TryParseStorageUrl(string? url, out string bucket, out string key)
        {
            bucket = AvatarBucket;
            key = string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return false;

            var value = url.Trim();
            if (value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
            {
                var rest = value[5..];
                var slash = rest.IndexOf('/');
                if (slash <= 0 || slash >= rest.Length - 1) return false;
                bucket = rest[..slash];
                key = rest[(slash + 1)..];
                return true;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
            var parts = uri.AbsolutePath.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                bucket = Uri.UnescapeDataString(parts[0]);
                key = Uri.UnescapeDataString(parts[1]);
                var q = key.IndexOf('?');
                if (q >= 0) key = key[..q];
                return !string.IsNullOrWhiteSpace(bucket) && !string.IsNullOrWhiteSpace(key);
            }
            return false;
        }
    }

    public class ChangeUserNameRequest
    {
        public string? CurrentUserName { get; set; }
        public string? NewUserName { get; set; }
        public string? CaptchaToken { get; set; }
    }

    public class ChangeEmailRequest
    {
        public string? CurrentEmail { get; set; }
        public string? NewEmail { get; set; }
        public string? CaptchaToken { get; set; }
    }

    public class ChangePasswordRequest
    {
        public string? CurrentPassword { get; set; }
        public string? NewPassword { get; set; }
        public string? CaptchaToken { get; set; }
    }

    public class DeleteAccountRequest
    {
        public string? CaptchaToken { get; set; }
    }
}
