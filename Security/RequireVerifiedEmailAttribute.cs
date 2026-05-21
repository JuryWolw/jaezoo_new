using System.Security.Claims;
using JaeZoo.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Security;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequireVerifiedEmailAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
            return;

        var id = user.FindFirstValue(ClaimTypes.NameIdentifier)
                 ?? user.FindFirstValue("sub")
                 ?? user.FindFirstValue("uid");

        if (!Guid.TryParse(id, out var userId))
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                code = "auth_required",
                message = "Не удалось определить пользователя. Войдите заново."
            });
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var confirmed = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.EmailConfirmed)
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (confirmed)
            return;

        context.Result = new ObjectResult(new
        {
            code = "email_not_verified",
            message = "Вы ещё не подтвердили почту. Подтвердите email в настройках аккаунта, чтобы пользоваться этой функцией JaeZoo."
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }
}
