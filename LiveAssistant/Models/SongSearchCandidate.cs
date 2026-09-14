namespace LiveAssistant.Models;

public sealed class SongSearchCandidate
{
    public string SongName { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Hash { get; set; } = "";
    public string SongId { get; set; } = "";
    public string AlbumId { get; set; } = "";
    public long AlbumAudioId { get; set; }

    public static SongSearchCandidate FromKugou(KugouSongItem song, string? keywordFallback = null)
        => new()
        {
            SongName = FirstNonEmpty(song.SongName, keywordFallback),
            Artist = song.Artist?.Trim() ?? "未知歌手",
            Hash = song.Hash?.Trim() ?? "",
            SongId = FirstNonEmpty(song.SongId, song.Id),
            AlbumId = song.AlbumId?.Trim() ?? "",
            AlbumAudioId = song.AlbumAudioId
        };

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }
}
