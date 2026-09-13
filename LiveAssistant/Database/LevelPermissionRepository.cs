using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class LevelPermissionRepository
{
    private readonly AppDatabase _db;

    public LevelPermissionRepository(AppDatabase db) => _db = db;

    public List<LevelPermission> ListAll()
    {
        var list = new List<LevelPermission>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT level, can_request, cooldown_seconds, min_points, queue_priority, points_cost_override
            FROM level_permissions ORDER BY level ASC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public LevelPermission? GetForLevel(int level)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT level, can_request, cooldown_seconds, min_points, queue_priority, points_cost_override
            FROM level_permissions WHERE level=$lvl LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$lvl", level);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void SaveAll(IEnumerable<LevelPermission> items)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM level_permissions";
            del.ExecuteNonQuery();
        }
        foreach (var item in items)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO level_permissions (level, can_request, cooldown_seconds, min_points, queue_priority, points_cost_override)
                VALUES ($lvl, $can, $cd, $min, $qp, $pco)
                """;
            cmd.Parameters.AddWithValue("$lvl", item.Level);
            cmd.Parameters.AddWithValue("$can", item.CanRequest ? 1 : 0);
            cmd.Parameters.AddWithValue("$cd", item.CooldownSeconds);
            cmd.Parameters.AddWithValue("$min", item.MinPoints);
            cmd.Parameters.AddWithValue("$qp", item.QueuePriority);
            cmd.Parameters.AddWithValue("$pco", item.PointsCostOverride);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static LevelPermission Read(SqliteDataReader reader) => new()
    {
        Level = reader.GetInt32(0),
        CanRequest = reader.GetInt64(1) == 1,
        CooldownSeconds = reader.GetInt32(2),
        MinPoints = reader.GetInt32(3),
        QueuePriority = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
        PointsCostOverride = reader.IsDBNull(5) ? -1 : reader.GetInt32(5)
    };
}
