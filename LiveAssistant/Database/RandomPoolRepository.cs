using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class RandomPoolRepository
{
    private readonly AppDatabase _db;

    public RandomPoolRepository(AppDatabase db) => _db = db;

    public List<RandomPoolItem> ListAll()
    {
        var list = new List<RandomPoolItem>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, song_name, artist, song_id, hash, keyword, enabled, created_at
            FROM random_pool_items ORDER BY id ASC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public List<RandomPoolItem> ListEnabled() =>
        ListAll().Where(x => x.Enabled).ToList();

    public long Add(RandomPoolItem item)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO random_pool_items (song_name, artist, song_id, hash, keyword, enabled, created_at)
            VALUES ($song, $artist, $sid, $hash, $kw, $en, $now)
            """;
        cmd.Parameters.AddWithValue("$song", item.SongName);
        cmd.Parameters.AddWithValue("$artist", item.Artist);
        cmd.Parameters.AddWithValue("$sid", item.SongId);
        cmd.Parameters.AddWithValue("$hash", item.Hash);
        cmd.Parameters.AddWithValue("$kw", item.Keyword);
        cmd.Parameters.AddWithValue("$en", item.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(idCmd.ExecuteScalar() ?? 0L);
    }

    public void Update(RandomPoolItem item)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE random_pool_items
            SET song_name=$song, artist=$artist, song_id=$sid, hash=$hash, keyword=$kw, enabled=$en
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$song", item.SongName);
        cmd.Parameters.AddWithValue("$artist", item.Artist);
        cmd.Parameters.AddWithValue("$sid", item.SongId);
        cmd.Parameters.AddWithValue("$hash", item.Hash);
        cmd.Parameters.AddWithValue("$kw", item.Keyword);
        cmd.Parameters.AddWithValue("$en", item.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.ExecuteNonQuery();
    }

    public bool Remove(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM random_pool_items WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static RandomPoolItem Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SongName = reader.IsDBNull(1) ? "" : reader.GetString(1),
        Artist = reader.IsDBNull(2) ? "" : reader.GetString(2),
        SongId = reader.IsDBNull(3) ? "" : reader.GetString(3),
        Hash = reader.IsDBNull(4) ? "" : reader.GetString(4),
        Keyword = reader.IsDBNull(5) ? "" : reader.GetString(5),
        Enabled = reader.GetInt64(6) == 1,
        CreatedAt = DateTime.TryParse(reader.GetString(7), out var dt) ? dt : DateTime.Now
    };
}
