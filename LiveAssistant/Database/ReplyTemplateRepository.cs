using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class ReplyTemplateRepository
{
    private readonly AppDatabase _db;

    public ReplyTemplateRepository(AppDatabase db) => _db = db;

    public Dictionary<string, string> GetAll()
    {
        var dict = new Dictionary<string, string>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT template_key, content FROM reply_templates";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.GetString(1);
        }
        return dict;
    }

    public void SaveAll(Dictionary<string, string> templates)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM reply_templates";
            del.Transaction = tx;
            del.ExecuteNonQuery();
        }
        foreach (var (key, content) in templates)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO reply_templates (template_key, content, updated_at)
                VALUES ($key, $content, $now)
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public int Count()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM reply_templates";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
