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
            SELECT id, gift_name, points, enabled, created_at FROM gift_rules ORDER BY id ASC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public int? GetPointsForGift(string giftName)
    {
        if (string.IsNullOrWhiteSpace(giftName))
        {
            return null;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT points FROM gift_rules
            WHERE enabled=1 AND gift_name=$name COLLATE NOCASE LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$name", giftName.Trim());
        var result = cmd.ExecuteScalar();
        return result == null ? null : Convert.ToInt32(result);
    }

    public long Add(GiftRule rule)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gift_rules (gift_name, points, enabled, created_at)
            VALUES ($name, $pts, $en, $now)
            """;
        cmd.Parameters.AddWithValue("$name", rule.GiftName);
        cmd.Parameters.AddWithValue("$pts", rule.Points);
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
            UPDATE gift_rules SET gift_name=$name, points=$pts, enabled=$en WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$name", rule.GiftName);
        cmd.Parameters.AddWithValue("$pts", rule.Points);
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
        GiftName = reader.GetString(1),
        Points = reader.GetInt32(2),
        Enabled = reader.GetInt64(3) == 1,
        CreatedAt = DateTime.TryParse(reader.GetString(4), out var dt) ? dt : DateTime.Now
    };
}
