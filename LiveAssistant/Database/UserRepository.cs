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
            INSERT INTO users (user_id, nickname, role, status, points, level, request_count, created_at, updated_at)
            VALUES ($uid, $nick, 'normal', 'active', 0, 0, 0, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$nick", nickname);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return new UserProfile
        {
            UserId = userId,
            Nickname = nickname,
            Role = UserRole.Normal,
            Status = UserStatus.Active
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
            SELECT user_id, nickname, role, status, points, level, request_count, last_request_at,
                   song_permission_credits, song_permission_unlimited
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

    public List<UserProfile> ListUsers(int limit = 200, int offset = 0)
    {
        var list = new List<UserProfile>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, nickname, role, status, points, level, request_count, last_request_at,
                   song_permission_credits, song_permission_unlimited
            FROM users ORDER BY points DESC, updated_at DESC LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadUser(reader));
        }
        return list;
    }

    public UserProfile? FindByNickname(string nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return null;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, nickname, role, status, points, level, request_count, last_request_at,
                   song_permission_credits, song_permission_unlimited
            FROM users WHERE nickname = $nick COLLATE NOCASE LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$nick", nickname.Trim());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
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

    public bool DeductPoints(string userId, int points)
    {
        if (string.IsNullOrWhiteSpace(userId) || points <= 0)
        {
            return false;
        }

        var user = GetUser(userId);
        if (user == null || user.Points < points)
        {
            return false;
        }

        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET points = points - $pts, updated_at = $now
            WHERE user_id = $uid AND points >= $pts
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$pts", points);
        cmd.Parameters.AddWithValue("$now", now);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void AddPoints(string userId, string nickname, int points)
    {
        if (string.IsNullOrWhiteSpace(userId) || points <= 0)
        {
            return;
        }

        EnsureUser(userId, nickname);
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users
            SET nickname = $nick, points = points + $pts, updated_at = $now
            WHERE user_id = $uid
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$nick", nickname);
        cmd.Parameters.AddWithValue("$pts", points);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public void SetPoints(string userId, int points)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET points = $pts, updated_at = $now WHERE user_id = $uid";
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$pts", points);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public void AdjustPoints(string userId, int delta)
    {
        if (string.IsNullOrWhiteSpace(userId) || delta == 0)
        {
            return;
        }

        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET points = MAX(0, points + $delta), updated_at = $now WHERE user_id = $uid
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$delta", delta);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public void SetLevel(string userId, int level)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET level = $lvl, updated_at = $now WHERE user_id = $uid";
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$lvl", level);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public void SetRole(string userId, UserRole role)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET role = $role, updated_at = $now WHERE user_id = $uid";
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$role", UserRoleExtensions.ToDb(role));
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public void SetStatus(string userId, UserStatus status)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET status = $status, updated_at = $now WHERE user_id = $uid";
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$status", UserStatusExtensions.ToDb(status));
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

    public void AddSongPermissionCredits(string userId, int credits)
    {
        if (string.IsNullOrWhiteSpace(userId) || credits <= 0)
        {
            return;
        }

        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET song_permission_credits = song_permission_credits + $credits, updated_at = $now
            WHERE user_id = $uid
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$credits", credits);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public void SetSongPermissionUnlimited(string userId, bool unlimited)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET song_permission_unlimited = $flag, updated_at = $now WHERE user_id = $uid
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$flag", unlimited ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public bool ConsumeSongPermissionCredit(string userId)
    {
        var user = GetUser(userId);
        if (user == null || user.SongPermissionUnlimited || user.SongPermissionCredits <= 0)
        {
            return false;
        }

        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET song_permission_credits = song_permission_credits - 1, updated_at = $now
            WHERE user_id = $uid AND song_permission_credits > 0
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$now", now);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static UserProfile ReadUser(SqliteDataReader reader)
    {
        DateTime? lastRequest = null;
        if (!reader.IsDBNull(7) && DateTime.TryParse(reader.GetString(7), out var dt))
        {
            lastRequest = dt;
        }

        return new UserProfile
        {
            UserId = reader.GetString(0),
            Nickname = reader.GetString(1),
            Role = UserRoleExtensions.FromDb(reader.IsDBNull(2) ? null : reader.GetString(2)),
            Status = UserStatusExtensions.FromDb(reader.IsDBNull(3) ? null : reader.GetString(3)),
            Points = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            Level = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            RequestCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            LastRequestAt = lastRequest,
            SongPermissionCredits = reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetInt32(8) : 0,
            SongPermissionUnlimited = reader.FieldCount > 9 && !reader.IsDBNull(9) && reader.GetInt64(9) == 1
        };
    }
}
