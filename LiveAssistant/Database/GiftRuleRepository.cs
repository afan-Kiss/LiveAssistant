using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class GiftRuleRepository
{
    private readonly AppDatabase _db;

    public GiftRuleRepository(AppDatabase db) => _db = db;

    public List<GiftRule> ListAll()
    {
        var list = new List<GiftRule>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, gift_id, gift_name, points, allow_song_request, enabled, created_at
            FROM gift_rules ORDER BY id ASC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public GiftRule? FindByGift(string giftName, string? giftId = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, gift_id, gift_name, points, allow_song_request, enabled, created_at
            FROM gift_rules WHERE enabled=1 AND (
                gift_name=$name COLLATE NOCASE OR ($gid != '' AND gift_id=$gid)
            ) LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$name", giftName.Trim());
        cmd.Parameters.AddWithValue("$gid", giftId ?? "");
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public int? GetPointsForGift(string giftName, string? giftId = null)
    {
        var rule = FindByGift(giftName, giftId);
        return rule?.Points;
    }

    public long Add(GiftRule rule)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gift_rules (gift_id, gift_name, points, allow_song_request, enabled, created_at)
            VALUES ($gid, $name, $pts, $allow, $en, $now)
            """;
        cmd.Parameters.AddWithValue("$gid", rule.GiftId);
        cmd.Parameters.AddWithValue("$name", rule.GiftName);
        cmd.Parameters.AddWithValue("$pts", rule.Points);
        cmd.Parameters.AddWithValue("$allow", rule.AllowSongRequest ? 1 : 0);
        cmd.Parameters.AddWithValue("$en", rule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(idCmd.ExecuteScalar() ?? 0L);
    }

    public void Update(GiftRule rule)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE gift_rules SET gift_id=$gid, gift_name=$name, points=$pts,
            allow_song_request=$allow, enabled=$en WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$gid", rule.GiftId);
        cmd.Parameters.AddWithValue("$name", rule.GiftName);
        cmd.Parameters.AddWithValue("$pts", rule.Points);
        cmd.Parameters.AddWithValue("$allow", rule.AllowSongRequest ? 1 : 0);
        cmd.Parameters.AddWithValue("$en", rule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", rule.Id);
        cmd.ExecuteNonQuery();
    }

    public bool Remove(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM gift_rules WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static GiftRule Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        GiftId = reader.IsDBNull(1) ? "" : reader.GetString(1),
        GiftName = reader.GetString(2),
        Points = reader.GetInt32(3),
        AllowSongRequest = reader.GetInt64(4) == 1,
        Enabled = reader.GetInt64(5) == 1,
        CreatedAt = DateTime.TryParse(reader.GetString(6), out var dt) ? dt : DateTime.Now
    };
}
