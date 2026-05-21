using JaeZoo.Server.Models;

namespace JaeZoo.Server.Security;

public static class AuthPolicies
{
    public const string OwnerOnly = "OwnerOnly";
    public const string AdminAccess = "AdminAccess";
    public const string ManageAds = "ManageAds";
    public const string ViewAdminAudit = "ViewAdminAudit";

    public static readonly string[] OwnerRoles = [GlobalRole.Owner.ToString()];

    public static readonly string[] AdminRoles =
    [
        GlobalRole.Owner.ToString(),
        GlobalRole.Admin.ToString()
    ];

    public static readonly string[] AdsManagerRoles =
    [
        GlobalRole.Owner.ToString(),
        GlobalRole.Admin.ToString(),
        GlobalRole.AdsManager.ToString()
    ];

    public static readonly string[] AuditViewerRoles =
    [
        GlobalRole.Owner.ToString(),
        GlobalRole.Admin.ToString()
    ];
}
