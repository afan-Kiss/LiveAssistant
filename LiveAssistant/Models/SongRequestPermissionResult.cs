namespace LiveAssistant.Models;

public sealed class SongRequestPermissionResult
{
    public bool Allowed { get; init; }
    public UserProfile? User { get; init; }
    public string? RejectReason { get; init; }
    public string? TemplateKey { get; init; }
    public Dictionary<string, string> TemplateVariables { get; init; } = new();

    public static SongRequestPermissionResult Permit(UserProfile user) => new()
    {
        Allowed = true,
        User = user
    };

    public static SongRequestPermissionResult Deny(
        UserProfile? user,
        string reason,
        string templateKey,
        Dictionary<string, string>? variables = null) => new()
    {
        Allowed = false,
        User = user,
        RejectReason = reason,
        TemplateKey = templateKey,
        TemplateVariables = variables ?? new Dictionary<string, string>()
    };
}
