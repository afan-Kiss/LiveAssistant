using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class WelcomeCooldownRepository
{
    private readonly AppDatabase _db;

    public WelcomeCooldownRepository(AppDatabase db) => _db = db;

    public bool ShouldWelcome(string userId, int cooldownSeconds)
    {
        if (cooldownSeconds <= 0 || string.IsNullOrWhiteSpace(userId))
        {
            return true;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT welcomed_at FROM welcome_records WHERE user_id = $uid";
        cmd.Parameters.AddWithValue("$uid", userId);
        var result = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(result) || !DateTime.TryParse(result, out var last))
        {
            return true;
        }

        return (DateTime.Now - last).TotalSeconds >= cooldownSeconds;
    }

    public void RecordWelcome(string userId)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO welcome_records (user_id, welcomed_at)
            VALUES ($uid, $now)
            ON CONFLICT(user_id) DO UPDATE SET welcomed_at = $now
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }
}
