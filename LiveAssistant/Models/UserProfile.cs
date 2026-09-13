namespace LiveAssistant.Models;

public sealed class UserProfile
{
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Normal;
    public int Points { get; set; }
    public int Level { get; set; }
    public int RequestCount { get; set; }
    public DateTime? LastRequestAt { get; set; }
}
