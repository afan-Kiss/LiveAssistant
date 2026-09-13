namespace LiveAssistant.Models;

public enum UserStatus
{
    Active,
    Muted,
    Banned
}

public static class UserStatusExtensions
{
    public static string ToDb(UserStatus status) => status switch
    {
        UserStatus.Muted => "muted",
        UserStatus.Banned => "banned",
        _ => "active"
    };

    public static UserStatus FromDb(string? value) => value switch
    {
        "muted" => UserStatus.Muted,
        "banned" => UserStatus.Banned,
        _ => UserStatus.Active
    };
}
