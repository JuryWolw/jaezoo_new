using JaeZoo.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using JaeZoo.Server.Models;

namespace JaeZoo.Server.Controllers.Admin;

[ApiController]
[Authorize(Policy = AuthPolicies.ModerationAccess)]
[Route("api/admin/reports")]
public sealed class AdminReportsController : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<AdminReportDto>> List()
    {
        // MVP: полноценные пользовательские жалобы пойдут следующим этапом.
        // Endpoint уже есть, чтобы админка имела стабильный контракт.
        return Array.Empty<AdminReportDto>();
    }
}
