using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services;
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

        public AccountController(AppDbContext db)
        {
            _db = db;
        }

        private Guid MeId
        {
            get
            {
                var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
                return Guid.TryParse(id, out var g) ? g : Guid.Empty;
            }
        }

        // PUT /api/users/account/username
        // Legacy route: фактически меняет приватный login. Публичный ник меняется через PUT /api/users/profile.
        [HttpPut("username")]
        public async Task<IActionResult> ChangeUserName([FromBody] ChangeUserNameRequest body, CancellationToken ct)
        {
            if (body == null) return BadRequest(new { message = "Body is required." });
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

            var normalized = UserIdentityService.NormalizeLogin(next);
            var exists = await _db.Users.AnyAsync(
                u => u.Id != me.Id && (u.LoginNormalized == normalized || u.UserName.ToUpper() == normalized), ct);
            if (exists) return Conflict(new { message = "Такой логин уже занят." });

            me.Login = next;
            me.LoginNormalized = normalized;
            me.UserName = next; // legacy sync
            me.SecurityStamp = UserIdentityService.NewSecurityStamp();
            me.TokenVersion++;
            me.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }

        // PUT /api/users/account/email
        [HttpPut("email")]
        public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest body, CancellationToken ct)
        {
            if (body == null) return BadRequest(new { message = "Body is required." });
            var current = (body.CurrentEmail ?? string.Empty).Trim();
            var next = (body.NewEmail ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(next))
                return BadRequest(new { message = "currentEmail and newEmail are required." });

            if (!new EmailAddressAttribute().IsValid(next))
                return UnprocessableEntity(new { message = "Некорректный адрес почты." });

            var me = await _db.Users.FirstOrDefaultAsync(u => u.Id == MeId, ct);
            if (me == null) return Unauthorized();

            if (!string.Equals(me.Email ?? string.Empty, current, StringComparison.OrdinalIgnoreCase))
                return NotFound(new { message = "Неверная текущая почта." });

            if (string.Equals(me.Email ?? string.Empty, next, StringComparison.OrdinalIgnoreCase))
                return Ok(new { message = "Почта не изменилась." });

            var normalized = UserIdentityService.NormalizeEmail(next);
            var exists = await _db.Users.AnyAsync(
                u => u.Id != me.Id && (u.EmailNormalized == normalized || u.Email.ToUpper() == normalized), ct);
            if (exists) return Conflict(new { message = "Эта почта уже используется." });

            me.Email = next;
            me.EmailNormalized = normalized;
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
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest body, CancellationToken ct)
        {
            if (body == null) return BadRequest(new { message = "Body is required." });
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
    }

    public class ChangeUserNameRequest
    {
        public string? CurrentUserName { get; set; }
        public string? NewUserName { get; set; }
    }

    public class ChangeEmailRequest
    {
        public string? CurrentEmail { get; set; }
        public string? NewEmail { get; set; }
    }

    public class ChangePasswordRequest
    {
        public string? CurrentPassword { get; set; }
        public string? NewPassword { get; set; }
    }
}
