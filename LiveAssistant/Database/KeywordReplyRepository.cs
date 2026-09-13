using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class KeywordReplyRepository
{
    private readonly AppDatabase _db;

    public KeywordReplyRepository(AppDatabase db) => _db = db;

    public List<KeywordReplyRule> ListAll()
    {
        var list = new List<KeywordReplyRule>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, keyword, template_key, reply_content, enabled, created_at
            FROM keyword_replies ORDER BY id DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public List<KeywordReplyRule> ListEnabled() => ListAll().Where(x => x.Enabled).ToList();

    public long Add(KeywordReplyRule rule)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO keyword_replies (keyword, template_key, reply_content, enabled, created_at)
            VALUES ($kw, $tpl, $reply, $en, $now)
            """;
        cmd.Parameters.AddWithValue("$kw", rule.Keyword);
        cmd.Parameters.AddWithValue("$tpl", rule.TemplateKey);
        cmd.Parameters.AddWithValue("$reply", rule.ReplyContent);
        cmd.Parameters.AddWithValue("$en", rule.Enabled ? 1 : 0);
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
        cmd.CommandText = "DELETE FROM keyword_replies WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void Update(KeywordReplyRule rule)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE keyword_replies SET keyword=$kw, template_key=$tpl, reply_content=$reply, enabled=$en WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$kw", rule.Keyword);
        cmd.Parameters.AddWithValue("$tpl", rule.TemplateKey);
        cmd.Parameters.AddWithValue("$reply", rule.ReplyContent);
        cmd.Parameters.AddWithValue("$en", rule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", rule.Id);
        cmd.ExecuteNonQuery();
    }

    private static KeywordReplyRule Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Keyword = reader.GetString(1),
        TemplateKey = reader.IsDBNull(2) ? "" : reader.GetString(2),
        ReplyContent = reader.IsDBNull(3) ? "" : reader.GetString(3),
        Enabled = reader.GetInt64(4) == 1,
        CreatedAt = DateTime.TryParse(reader.GetString(5), out var dt) ? dt : DateTime.Now
    };
}
