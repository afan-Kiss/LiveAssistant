namespace LiveAssistant.Models;

public enum UserRole
{
    Normal,
    Admin,
    Manager,
    Blacklist
}

public static class UserRoleExtensions
{
    public static string ToDb(UserRole role) => role switch
    {
        UserRole.Admin => "admin",
        UserRole.Manager => "manager",
        UserRole.Blacklist => "blacklist",
        _ => "normal"
    };

    public static UserRole FromDb(string? value) => value switch
    {
        "admin" => UserRole.Admin,
        "manager" => UserRole.Manager,
        "blacklist" => UserRole.Blacklist,
        _ => UserRole.Normal
    };
}
