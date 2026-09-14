using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class GiftRepository
{
    private readonly AppDatabase _db;

    public GiftRepository(AppDatabase db) => _db = db;

    public bool ExistsByEventId(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM gift_events WHERE event_id = $eid";
        cmd.Parameters.AddWithValue("$eid", eventId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public bool TryInsert(GiftEvent gift, int pointsDelta, int pointsAfter, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(gift.EventId))
        {
            return false;
        }

        if (ExistsByEventId(gift.EventId))
        {
            return false;
        }

        var now = gift.Time.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gift_events (event_id, user_id, nickname, gift_id, gift_name, count, value, points_delta, points_after, created_at)
            VALUES ($eid, $uid, $nick, $gid, $gname, $cnt, $val, $pd, $pa, $now)
            """;
        cmd.Parameters.AddWithValue("$eid", gift.EventId);
        cmd.Parameters.AddWithValue("$uid", gift.UserId);
        cmd.Parameters.AddWithValue("$nick", gift.Nickname);
        cmd.Parameters.AddWithValue("$gid", gift.GiftId);
        cmd.Parameters.AddWithValue("$gname", gift.GiftName);
        cmd.Parameters.AddWithValue("$cnt", gift.Count);
        cmd.Parameters.AddWithValue("$val", gift.Value);
        cmd.Parameters.AddWithValue("$pd", pointsDelta);
        cmd.Parameters.AddWithValue("$pa", pointsAfter);
        cmd.Parameters.AddWithValue("$now", now);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            return false;
        }

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        id = (long)(idCmd.ExecuteScalar() ?? 0L);
        return true;
    }

    public List<GiftEvent> ListRecent(int limit = 50)
    {
        var list = new List<GiftEvent>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_id, user_id, nickname, gift_id, gift_name, count, value, created_at
            FROM gift_events ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GiftEvent
            {
                Id = reader.GetInt64(0),
                EventId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                UserId = reader.GetString(2),
                Nickname = reader.GetString(3),
                GiftId = reader.GetString(4),
                GiftName = reader.GetString(5),
                Count = reader.GetInt32(6),
                Value = reader.GetInt32(7),
                Time = DateTime.TryParse(reader.GetString(8), out var dt) ? dt : DateTime.Now,
                CreatedAt = DateTime.TryParse(reader.GetString(8), out var c) ? c : DateTime.Now
            });
        }
        return list;
    }
}
