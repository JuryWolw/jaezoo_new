using System.ComponentModel.DataAnnotations;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController(AppDbContext db, TokenService tokens) : ControllerBase
{
    private readonly PasswordHasher<User> _hasher = new();

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest r, CancellationToken ct)
    {
        var login = (r.Login ?? string.Empty).Trim();
        var email = (r.Email ?? string.Empty).Trim();
        var password = r.Password ?? string.Empty;
        var confirmPassword = r.ConfirmPassword ?? string.Empty;

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(confirmPassword))
            return BadRequest("Заполните логин, почту и пароль.");

        if (!UserIdentityService.IsValidLogin(login))
            return BadRequest("Логин должен быть 3-32 символа: латиница, цифры, точка, дефис или подчёркивание.");

        if (!new EmailAddressAttribute().IsValid(email))
            return BadRequest("Некорректный адрес почты.");

        if (password.Length < 8)
            return BadRequest("Пароль должен быть не короче 8 символов.");

        if (password != confirmPassword)
            return BadRequest("Пароли не совпадают.");

        var loginNormalized = UserIdentityService.NormalizeLogin(login);
        var emailNormalized = UserIdentityService.NormalizeEmail(email);

        if (await db.Users.AnyAsync(u => u.LoginNormalized == loginNormalized, ct))
            return Conflict("Пользователь с таким логином уже существует.");

        if (await db.Users.AnyAsync(u => u.EmailNormalized == emailNormalized, ct))
            return Conflict("Пользователь с такой почтой уже существует.");

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = login, // legacy поле: пока держим приватный login для старого кода/миграций
            Login = login,
            LoginNormalized = loginNormalized,
            Email = email,
            EmailNormalized = emailNormalized,
            EmailConfirmed = false,
            EmailVerifiedAt = null,
            PublicId = await UserIdentityService.CreateUniquePublicIdAsync(db, ct),
            DisplayName = UserIdentityService.CreateRandomDisplayName(),
            AvatarUrl = null,
            CreatedAt = now,
            UpdatedAt = now,
            SecurityStamp = UserIdentityService.NewSecurityStamp(),
            TokenVersion = 0,
            IsDisabled = false
        };

        user.PasswordHash = _hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return Created("", new
        {
            message = "Регистрация успешна. Войдите и подтвердите почту.",
            publicId = user.PublicId,
            displayName = user.DisplayName,
            emailConfirmed = user.EmailConfirmed
        });
    }

    [HttpPost("login")]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest r, CancellationToken ct)
    {
        var loginOrEmail = r.GetLoginOrEmail();
        var password = r.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(loginOrEmail) || string.IsNullOrWhiteSpace(password))
            return Unauthorized("Неверный логин/почта или пароль.");

        var normalized = loginOrEmail.Contains('@')
            ? UserIdentityService.NormalizeEmail(loginOrEmail)
            : UserIdentityService.NormalizeLogin(loginOrEmail);

        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.LoginNormalized == normalized ||
            u.EmailNormalized == normalized, ct);

        if (user is null)
            return Unauthorized("Неверный логин/почта или пароль.");

        if (user.IsDisabled)
            return StatusCode(StatusCodes.Status403Forbidden, string.IsNullOrWhiteSpace(user.DisabledReason)
                ? "Аккаунт отключён."
                : user.DisabledReason);

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result is PasswordVerificationResult.Failed)
            return Unauthorized("Неверный логин/почта или пароль.");

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = _hasher.HashPassword(user, password);

        user.Login = string.IsNullOrWhiteSpace(user.Login) ? user.UserName : user.Login;
        user.LoginNormalized = string.IsNullOrWhiteSpace(user.LoginNormalized) ? UserIdentityService.NormalizeLogin(user.Login) : user.LoginNormalized;
        user.EmailNormalized = string.IsNullOrWhiteSpace(user.EmailNormalized) ? UserIdentityService.NormalizeEmail(user.Email) : user.EmailNormalized;
        user.DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? UserIdentityService.CreateRandomDisplayName() : user.DisplayName;
        user.PublicId = string.IsNullOrWhiteSpace(user.PublicId) ? await UserIdentityService.CreateUniquePublicIdAsync(db, ct) : user.PublicId;
        user.SecurityStamp = string.IsNullOrWhiteSpace(user.SecurityStamp) ? UserIdentityService.NewSecurityStamp() : user.SecurityStamp;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var roles = await db.UserRoles
            .Where(r => r.UserId == user.Id && r.RevokedAt == null)
            .Select(r => r.Role)
            .ToListAsync(ct);

        var token = tokens.Create(user, roles);
        return new TokenResponse(token, ToUserDto(user), roles.Select(r => r.ToString()).ToList());
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        var idStr = User.Claims.First(c => c.Type == "sub").Value;
        var id = Guid.Parse(idStr);
        var u = await db.Users.FindAsync(new object?[] { id }, ct);
        if (u is null) return NotFound();
        return ToUserDto(u);
    }

    private static UserDto ToUserDto(User u) => new(
        u.Id,
        UserIdentityService.GetPublicName(u),
        u.Email,
        u.CreatedAt,
        UserIdentityService.GetLogin(u),
        UserIdentityService.GetPublicName(u),
        u.PublicId,
        u.EmailConfirmed,
        u.EmailVerifiedAt,
        UserIdentityService.GetAvatarUrl(u));
}
