using JaeZoo.Server.Models;

namespace JaeZoo.Server.Security;

/// <summary>
/// Отметка входа персонала в токене. Выдаёт только StaffAuthController после пароля, кода с почты и 2FA;
/// обычный вход, вход с 2FA и обновление токена её не ставят.
/// </summary>
public static class StaffClaims
{
    public const string App = "jz_staff";
    public const string AdminApp = "admin";
    public const string AdsApp = "ads";
}

public static class AuthPolicies
{
    public const string OwnerOnly = "OwnerOnly";
    public const string AdminAccess = "AdminAccess";
    public const string ManageAds = "ManageAds";
    public const string PublishAds = "PublishAds";
    public const string ViewAdminAudit = "ViewAdminAudit";
    public const string AdminPanelAccess = "AdminPanelAccess";
    public const string ModerationAccess = "ModerationAccess";

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
        GlobalRole.Admin.ToString(),
        GlobalRole.Moderator.ToString(),
        GlobalRole.Helper.ToString()
    ];

    public static readonly string[] AdminPanelRoles =
    [
        GlobalRole.Owner.ToString(),
        GlobalRole.Admin.ToString(),
        GlobalRole.Moderator.ToString(),
        GlobalRole.Helper.ToString()
    ];

    public static readonly string[] ModerationRoles =
    [
        GlobalRole.Owner.ToString(),
        GlobalRole.Admin.ToString(),
        GlobalRole.Moderator.ToString(),
        GlobalRole.Helper.ToString()
    ];
}
