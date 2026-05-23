using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services;
using JaeZoo.Server.Services.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/auth/email")]
[EnableRateLimiting("auth")]
public sealed class AuthEmailController(
    AppDbContext db,
    EmailVerificationService emailVerification,
    ILogger<AuthEmailController> log) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<EmailVerificationStatusDto>> Status(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized("Не удалось определить пользователя по токену.");

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return NotFound("Пользователь не найден.");

        return new EmailVerificationStatusDto(UserIdentityService.GetEmail(user), user.EmailConfirmed, user.EmailVerifiedAt);
    }

    [HttpPost("resend")]
    public async Task<ActionResult<ResendEmailConfirmationResponse>> Resend(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized("Не удалось определить пользователя по токену.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return NotFound("Пользователь не найден.");

        if (user.EmailConfirmed)
            return BadRequest("Почта уже подтверждена.");

        try
        {
            var result = await emailVerification.SendConfirmationCodeAsync(user, HttpContext, ct);
            var response = new ResendEmailConfirmationResponse(
                result.Sent,
                result.Cooldown,
                result.RetryAfterSeconds,
                result.ExpiresAt,
                result.Message);

            if (result.Cooldown)
                return StatusCode(StatusCodes.Status429TooManyRequests, response);

            return response;
        }
        catch (InvalidOperationException ex)
        {
            log.LogWarning(ex, "Email confirmation resend rejected. UserId={UserId}", userId);
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to send email confirmation code. UserId={UserId} EmailHash={EmailHash}", userId, user.EmailHash);

            var message = ex is TimeoutException
                ? "Ошибка отправки кода: Yandex Cloud Postbox не ответил за отведённое время."
                : $"Ошибка отправки кода: {ex.Message}";

            if (message.Length > 500)
                message = message[..500];

            return StatusCode(StatusCodes.Status500InternalServerError, message);
        }
    }

    [HttpPost("confirm")]
    public async Task<ActionResult<UserDto>> Confirm([FromBody] ConfirmEmailRequest request, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized("Не удалось определить пользователя по токену.");

        try
        {
            var user = await emailVerification.ConfirmEmailAsync(userId, request.Code ?? string.Empty, ct);
            return ToUserDto(user);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to confirm email. UserId={UserId}", userId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Ошибка подтверждения почты.");
        }
    }

    private bool TryGetCurrentUserId(out Guid id)
    {
        id = Guid.Empty;

        var candidates = new[]
        {
            User.FindFirstValue(ClaimTypes.NameIdentifier),
            User.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"),
            User.FindFirstValue("sub"),
            User.FindFirstValue("nameid")
        };

        foreach (var value in candidates)
        {
            if (Guid.TryParse(value, out id))
                return true;
        }

        log.LogWarning("User id claim is missing or invalid. Claims={Claims}",
            string.Join(", ", User.Claims.Select(c => c.Type + "=" + c.Value)));
        return false;
    }

    private static UserDto ToUserDto(User u) => new(
        u.Id,
        UserIdentityService.GetPublicName(u),
        UserIdentityService.GetEmail(u),
        u.CreatedAt,
        UserIdentityService.GetLogin(u),
        UserIdentityService.GetPublicName(u),
        u.PublicId,
        u.EmailConfirmed,
        u.EmailVerifiedAt,
        UserIdentityService.GetAvatarUrl(u));
}
