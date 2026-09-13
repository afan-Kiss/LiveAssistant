using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class UserRepository
{
    private readonly AppDatabase _db;

    public UserRepository(AppDatabase db)
    {
        _db = db;
    }

    public UserProfile EnsureUser(string userId, string nickname)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new UserProfile { UserId = userId, Nickname = nickname };
        }

        var existing = GetUser(userId);
        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(nickname) && existing.Nickname != nickname)
            {
                UpdateNickname(userId, nickname);
                existing.Nickname = nickname;
            }
            return existing;
        }

        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (user_id, nickname, role, points, level, request_count, created_at, updated_at)
            VALUES ($uid, $nick, 'normal', 0, 0, 0, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$nick", nickname);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return new UserProfile
        {
            UserId = userId,
            Nickname = nickname,
            Role = UserRole.Normal
        };
    }

    public UserProfile? GetUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, nickname, role, points, level, request_count, last_request_at
            FROM users WHERE user_id = $uid
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ReadUser(reader);
    }

    public (bool Allowed, int RemainingSeconds) CheckCooldown(string userId, int cooldownSeconds)
    {
        if (cooldownSeconds <= 0 || string.IsNullOrWhiteSpace(userId))
        {
            return (true, 0);
        }

        var user = GetUser(userId);
        if (user?.LastRequestAt == null)
        {
            return (true, 0);
        }

        var elapsed = DateTime.Now - user.LastRequestAt.Value;
        if (elapsed.TotalSeconds >= cooldownSeconds)
        {
            return (true, 0);
        }

        var remaining = (int)Math.Ceiling(cooldownSeconds - elapsed.TotalSeconds);
        return (false, Math.Max(1, remaining));
    }

    public void RecordSuccessfulRequest(string userId, string nickname)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        EnsureUser(userId, nickname);
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users
            SET nickname = $nick,
                request_count = request_count + 1,
                last_request_at = $now,
                updated_at = $now
            WHERE user_id = $uid
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$nick", nickname);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    private void UpdateNickname(string userId, string nickname)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET nickname = $nick, updated_at = $now WHERE user_id = $uid";
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$nick", nickname);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static UserProfile ReadUser(SqliteDataReader reader)
    {
        DateTime? lastRequest = null;
        if (!reader.IsDBNull(6) && DateTime.TryParse(reader.GetString(6), out var dt))
        {
            lastRequest = dt;
        }

        return new UserProfile
        {
            UserId = reader.GetString(0),
            Nickname = reader.GetString(1),
            Role = UserRoleExtensions.FromDb(reader.IsDBNull(2) ? null : reader.GetString(2)),
            Points = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            Level = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            RequestCount = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            LastRequestAt = lastRequest
        };
    }
}
