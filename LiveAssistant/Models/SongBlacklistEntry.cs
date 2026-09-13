namespace LiveAssistant.Models;

public sealed class SongBlacklistEntry
{
    public long Id { get; set; }
    public string SongName { get; set; } = "";
    public string Artist { get; set; } = "";
    public string SongId { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
