using LiveAssistant.Models;

namespace LiveAssistant.Services;

internal sealed class KugouSongContext
{
    public string? Hash { get; init; }
    public string? Keyword { get; init; }
    public string? AlbumId { get; init; }
    public long AlbumAudioId { get; init; }

    public static KugouSongContext FromSong(KugouSongItem song, string? keyword = null)
        => new()
        {
            Hash = song.Hash?.Trim(),
            Keyword = string.IsNullOrWhiteSpace(keyword) ? song.SongName?.Trim() : keyword.Trim(),
            AlbumId = song.AlbumId?.Trim(),
            AlbumAudioId = song.AlbumAudioId
        };

    public static KugouSongContext FromHash(string? hash, string? keyword = null)
        => new()
        {
            Hash = hash?.Trim(),
            Keyword = keyword?.Trim()
        };
}
