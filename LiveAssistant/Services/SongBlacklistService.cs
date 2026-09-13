using LiveAssistant.Database;

namespace LiveAssistant.Services;

public sealed class SongBlacklistService
{
    private readonly SongBlacklistRepository _repo;

    public SongBlacklistService(SongBlacklistRepository repo)
    {
        _repo = repo;
    }

    public bool IsBlocked(string songName, string? songId = null) =>
        _repo.IsBlocked(songName, songId);
}
