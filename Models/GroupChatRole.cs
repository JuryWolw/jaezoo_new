namespace JaeZoo.Server.Models;

public enum GroupChatRole
{
    Member = 0,
    Helper = 1,
    Moderator = 2,
    Admin = 3
}

public static class GroupChatRoleInfo
{
    public static string GetDisplayName(GroupChatRole role) => role switch
    {
        GroupChatRole.Admin => "Админ",
        GroupChatRole.Moderator => "Модератор",
        GroupChatRole.Helper => "Хелпер",
        _ => "Участник"
    };

    public static string GetColorHex(GroupChatRole role) => role switch
    {
        GroupChatRole.Admin => "#FF4D4F",
        GroupChatRole.Moderator => "#4F7CFF",
        GroupChatRole.Helper => "#34C759",
        _ => "#9AA0A6"
    };

    public static string GetColorName(GroupChatRole role) => role switch
    {
        GroupChatRole.Admin => "Red",
        GroupChatRole.Moderator => "Blue",
        GroupChatRole.Helper => "Green",
        _ => "Gray"
    };

    public static bool TryParse(string? raw, out GroupChatRole role)
    {
        role = GroupChatRole.Member;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return Enum.TryParse(raw.Trim(), true, out role);
    }
}
