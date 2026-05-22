using JaeZoo.Server.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JaeZoo.Server.Security;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequireRiskCaptchaAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string _action;
    private readonly int _maxActions;
    private readonly int _windowSeconds;

    public RequireRiskCaptchaAttribute(string action, int maxActions, int windowSeconds)
    {
        _action = action;
        _maxActions = maxActions;
        _windowSeconds = windowSeconds;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var service = context.HttpContext.RequestServices.GetRequiredService<RiskCaptchaService>();
        var result = await service.CheckAsync(
            context.HttpContext,
            _action,
            Math.Max(1, _maxActions),
            TimeSpan.FromSeconds(Math.Max(5, _windowSeconds)),
            context.HttpContext.RequestAborted);

        if (result.Success)
            return;

        context.Result = new ObjectResult(new
        {
            code = "captcha_required",
            message = string.IsNullOrWhiteSpace(result.Message)
                ? "Подтвердите, что вы человек."
                : result.Message
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }
}
