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
    public async Task<IActionResult> Register([FromBody] RegisterRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.UserName) || string.IsNullOrWhiteSpace(r.Email) ||
            string.IsNullOrWhiteSpace(r.Password) || string.IsNullOrWhiteSpace(r.ConfirmPassword))
            return BadRequest("Заполните все поля.");

        if (r.Password != r.ConfirmPassword)
            return BadRequest("Пароли не совпадают.");

        if (await db.Users.AnyAsync(u => u.UserName == r.UserName))
            return Conflict("Пользователь с таким логином уже существует.");

        if (await db.Users.AnyAsync(u => u.Email == r.Email))
            return Conflict("Пользователь с такой почтой уже существует.");

        var user = new User { UserName = r.UserName.Trim(), Email = r.Email.Trim() };
        user.PasswordHash = _hasher.HashPassword(user, r.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return Created("", new { message = "Регистрация успешна. Теперь войдите." });
    }

    [HttpPost("login")]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest r)
    {
        var login = (r.LoginOrEmail ?? "").Trim();
        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.UserName == login || u.Email == login);

        if (user is null)
            return Unauthorized("Неверный логин/почта или пароль.");

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, r.Password);
        if (result is not PasswordVerificationResult.Success)
            return Unauthorized("Неверный логин/почта или пароль.");

        var token = tokens.Create(user);
        var dto = new UserDto(user.Id, user.UserName, user.Email, user.CreatedAt);
        return new TokenResponse(token, dto);
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me([FromServices] AppDbContext db)
    {
        var idStr = User.Claims.First(c => c.Type == "sub").Value;
        var id = Guid.Parse(idStr);
        var u = await db.Users.FindAsync(id);
        if (u is null) return NotFound();
        return new UserDto(u.Id, u.UserName, u.Email, u.CreatedAt);
    }
}
