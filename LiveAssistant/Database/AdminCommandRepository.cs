using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class AdminCommandRepository
{
    private readonly AppDatabase _db;

    public AdminCommandRepository(AppDatabase db)
    {
        _db = db;
    }

    public long Enqueue(AdminCommandType type, string? payload = null)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO admin_commands (command_type, payload, status, created_at)
            VALUES ($type, $payload, 'pending', $now)
            """;
        cmd.Parameters.AddWithValue("$type", type.ToString());
        cmd.Parameters.AddWithValue("$payload", payload ?? "");
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(idCmd.ExecuteScalar() ?? 0L);
    }

    public List<AdminCommand> DequeuePending(int limit = 20)
    {
        var list = new List<AdminCommand>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, command_type, payload, created_at
            FROM admin_commands WHERE status = 'pending'
            ORDER BY id ASC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AdminCommand
            {
                Id = reader.GetInt64(0),
                Type = Enum.Parse<AdminCommandType>(reader.GetString(1)),
                Payload = reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedAt = DateTime.TryParse(reader.GetString(3), out var dt) ? dt : DateTime.Now
            });
        }

        if (list.Count == 0)
        {
            return list;
        }

        var ids = string.Join(",", list.Select(x => x.Id));
        using var upd = conn.CreateCommand();
        upd.CommandText = $"UPDATE admin_commands SET status = 'processed', processed_at = $now WHERE id IN ({ids})";
        upd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        upd.ExecuteNonQuery();
        return list;
    }
}
