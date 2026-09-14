namespace LiveAssistant.Models;

public enum SongRequestSessionStep
{
    ChooseArtist,
    Confirm
}

public sealed class SongRequestSession
{
    public required string UserId { get; init; }
    public required string Nickname { get; init; }
    public required string Keyword { get; init; }
    public SongRequestSessionStep Step { get; set; }
    public List<SongSearchCandidate> Candidates { get; init; } = new();
    public SongSearchCandidate? Selected { get; set; }
    public DateTime ExpiresAt { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
}
