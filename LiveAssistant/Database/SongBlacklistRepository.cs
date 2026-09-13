using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class SongBlacklistRepository
{
    private readonly AppDatabase _db;

    public SongBlacklistRepository(AppDatabase db)
    {
        _db = db;
    }

    public List<SongBlacklistEntry> ListAll()
    {
        var list = new List<SongBlacklistEntry>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, song_name, artist, song_id, reason, created_at
            FROM song_blacklist ORDER BY id DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public long Add(SongBlacklistEntry entry)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO song_blacklist (song_name, artist, song_id, reason, created_at)
            VALUES ($song, $artist, $sid, $reason, $now)
            """;
        cmd.Parameters.AddWithValue("$song", entry.SongName);
        cmd.Parameters.AddWithValue("$artist", entry.Artist);
        cmd.Parameters.AddWithValue("$sid", entry.SongId);
        cmd.Parameters.AddWithValue("$reason", entry.Reason);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(idCmd.ExecuteScalar() ?? 0L);
    }

    public bool Remove(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM song_blacklist WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool IsBlocked(string songName, string? songId = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM song_blacklist
            WHERE song_name = $song COLLATE NOCASE
               OR ($sid != '' AND song_id = $sid)
            """;
        cmd.Parameters.AddWithValue("$song", songName.Trim());
        cmd.Parameters.AddWithValue("$sid", songId ?? "");
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static SongBlacklistEntry Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SongName = reader.GetString(1),
        Artist = reader.IsDBNull(2) ? "" : reader.GetString(2),
        SongId = reader.IsDBNull(3) ? "" : reader.GetString(3),
        Reason = reader.IsDBNull(4) ? "" : reader.GetString(4),
        CreatedAt = DateTime.TryParse(reader.GetString(5), out var dt) ? dt : DateTime.Now
    };
}
