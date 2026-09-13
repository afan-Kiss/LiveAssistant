using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Services;

public sealed class RandomPlaylistService
{
    private readonly ConfigManager _config;
    private readonly AppDatabase _db;
    private readonly Random _random = new();

    public RandomPlaylistService(ConfigManager config, AppDatabase db)
    {
        _config = config;
        _db = db;
    }

    public RandomPlaylistItem? PickNext()
    {
        var settings = _config.Settings.RandomPlaylist;
        var items = settings.Items.Where(x =>
            !string.IsNullOrWhiteSpace(x.Keyword) ||
            !string.IsNullOrWhiteSpace(x.Hash) ||
            !string.IsNullOrWhiteSpace(x.Title)).ToList();

        if (items.Count == 0)
        {
            return null;
        }

        var eligible = items.Where(x => !WasRecentlyPlayed(x, settings.NoRepeatMinutes)).ToList();
        if (eligible.Count == 0)
        {
            eligible = items;
        }

        if (settings.Shuffle)
        {
            return eligible[_random.Next(eligible.Count)];
        }

        return eligible[0];
    }

    public void RecordPlayed(string? songId, string? hash)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO random_play_history (song_id, hash, played_at)
            VALUES ($sid, $hash, $at)
            """;
        cmd.Parameters.AddWithValue("$sid", songId ?? "");
        cmd.Parameters.AddWithValue("$hash", hash ?? "");
        cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private bool WasRecentlyPlayed(RandomPlaylistItem item, int noRepeatMinutes)
    {
        if (noRepeatMinutes <= 0)
        {
            return false;
        }

        var cutoff = DateTime.Now.AddMinutes(-noRepeatMinutes);
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM random_play_history
            WHERE played_at >= $cutoff
              AND (
                ($sid <> '' AND song_id = $sid)
                OR ($hash <> '' AND hash = $hash)
              )
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        cmd.Parameters.AddWithValue("$sid", item.SongId ?? "");
        cmd.Parameters.AddWithValue("$hash", item.Hash ?? "");
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
