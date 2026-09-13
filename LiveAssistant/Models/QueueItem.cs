namespace LiveAssistant.Models;

public sealed class QueueItem
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string SongName { get; set; } = "";
    public string Artist { get; set; } = "";
    public string SongId { get; set; } = "";
    public string Hash { get; set; } = "";
    public string? PlayUrl { get; set; }
    public bool IsRandom { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string DisplayLine => IsRandom
        ? $"🎵 {SongName} - {Artist}"
        : $"{Nickname} - {SongName}";
}
