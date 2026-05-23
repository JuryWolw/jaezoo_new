using JaeZoo.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JaeZoo.Server.Controllers.Admin;

[ApiController]
[Authorize(Policy = AuthPolicies.OwnerOnly)]
[Route("api/admin/security")]
public sealed class AdminSecurityController(IConfiguration configuration, IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet("health")]
    public ActionResult<SecurityHealthDto> GetHealth()
    {
        var report = SecurityStartupValidator.Evaluate(configuration, environment);
        var items = report.Checks
            .Select(x => new SecurityHealthCheckDto(x.Area, x.Name, x.Ok, x.Required, x.Message))
            .OrderBy(x => x.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SecurityHealthDto(
            report.Ok,
            environment.EnvironmentName,
            report.Production,
            report.StrictMode,
            DateTime.UtcNow,
            items);
    }
}

public sealed record SecurityHealthDto(
    bool Ok,
    string Environment,
    bool Production,
    bool StrictMode,
    DateTime CheckedAtUtc,
    IReadOnlyList<SecurityHealthCheckDto> Checks);

public sealed record SecurityHealthCheckDto(
    string Area,
    string Name,
    bool Ok,
    bool Required,
    string Message);
