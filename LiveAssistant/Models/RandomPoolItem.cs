namespace LiveAssistant.Models;

public sealed class RandomPoolItem
{
    public long Id { get; set; }
    public string SongName { get; set; } = "";
    public string Artist { get; set; } = "";
    public string SongId { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Keyword { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
