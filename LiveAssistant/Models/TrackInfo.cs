namespace LiveAssistant.Models;

public sealed class TrackInfo
{
    public string SongName { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Requester { get; set; } = "";
    public string SongId { get; set; } = "";
    public string Hash { get; set; } = "";
    public string AlbumId { get; set; } = "";
    public long AlbumAudioId { get; set; }
    public bool IsPreview { get; set; }
    public string? PlayUrl { get; set; }
    public bool IsRandom { get; set; }
    public int DurationSec { get; set; }
}
