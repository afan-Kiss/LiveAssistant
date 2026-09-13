using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class GiftRepository
{
    private readonly AppDatabase _db;

    public GiftRepository(AppDatabase db)
    {
        _db = db;
    }

    public long Insert(GiftEvent gift)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gift_events (user_id, nickname, gift_id, gift_name, count, value, created_at)
            VALUES ($uid, $nick, $gid, $gname, $cnt, $val, $now)
            """;
        cmd.Parameters.AddWithValue("$uid", gift.UserId);
        cmd.Parameters.AddWithValue("$nick", gift.Nickname);
        cmd.Parameters.AddWithValue("$gid", gift.GiftId);
        cmd.Parameters.AddWithValue("$gname", gift.GiftName);
        cmd.Parameters.AddWithValue("$cnt", gift.Count);
        cmd.Parameters.AddWithValue("$val", gift.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(idCmd.ExecuteScalar() ?? 0L);
    }

    public List<GiftEvent> ListRecent(int limit = 50)
    {
        var list = new List<GiftEvent>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, nickname, gift_id, gift_name, count, value, created_at
            FROM gift_events ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GiftEvent
            {
                Id = reader.GetInt64(0),
                UserId = reader.GetString(1),
                Nickname = reader.GetString(2),
                GiftId = reader.GetString(3),
                GiftName = reader.GetString(4),
                Count = reader.GetInt32(5),
                Value = reader.GetInt32(6),
                CreatedAt = DateTime.TryParse(reader.GetString(7), out var dt) ? dt : DateTime.Now
            });
        }
        return list;
    }
}
