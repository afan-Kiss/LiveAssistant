using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class UserRepository
{
    private readonly AppDatabase _db;
    private readonly PointsLedgerRepository _ledger;

    public UserRepository(AppDatabase db, PointsLedgerRepository? ledger = null)
    {
        _db = db;
        _ledger = ledger ?? new PointsLedgerRepository(db);
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

        // 并发安全：INSERT OR IGNORE，避免两条弹幕同时 EnsureUser 触发 UNIQUE 冲突
        var now = DateTime.Now.ToString("O");
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO users (user_id, nickname, role, status, points, level, request_count, created_at, updated_at)
                VALUES ($uid, $nick, 'normal', 'active', 0, 0, 0, $now, $now)
                """;
            cmd.Parameters.AddWithValue("$uid", userId);
            cmd.Parameters.AddWithValue("$nick", string.IsNullOrWhiteSpace(nickname) ? userId : nickname);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        var user = GetUser(userId);
        if (user == null)
        {
            // 极端情况下读不到则返回内存画像，避免丢弹幕
            return new UserProfile
            {
                UserId = userId,
                Nickname = nickname,
                Role = UserRole.Normal,
                Status = UserStatus.Active
            };
        }

        if (!string.IsNullOrWhiteSpace(nickname) && user.Nickname != nickname)
        {
            UpdateNickname(userId, nickname);
            user.Nickname = nickname;
        }

        return user;
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
                   song_permission_credits, song_permission_unlimited, updated_at
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
                   song_permission_credits, song_permission_unlimited, updated_at
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

    public List<UserProfile> SearchUsers(string query, int limit = 100)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return ListUsers(limit);
        }

        var list = new List<UserProfile>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, nickname, role, status, points, level, request_count, last_request_at,
                   song_permission_credits, song_permission_unlimited, updated_at
            FROM users
            WHERE nickname LIKE $q COLLATE NOCASE OR user_id LIKE $q
            ORDER BY points DESC, updated_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$q", "%" + query + "%");
        cmd.Parameters.AddWithValue("$limit", limit);
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
                   song_permission_credits, song_permission_unlimited, updated_at
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
        => TryDeductPoints(userId, "", points, "deduct", "积分扣除", null, out _);

    public bool TryDeductPoints(
        string userId,
        string nickname,
        int points,
        string type,
        string reason,
        string? refId,
        out int balanceAfter)
        => TryChangePoints(userId, nickname, -points, type, reason, refId, out balanceAfter);

    public void AddPoints(string userId, string nickname, int points)
    {
        if (string.IsNullOrWhiteSpace(userId) || points <= 0)
        {
            return;
        }

        TryChangePoints(userId, nickname, points, PointsTransactionType.AdminAdjust, "积分增加", null, out _);
    }

    public bool TryAdminSetPoints(string userId, int points, string reason, string operatorName, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(userId))
        {
            error = "用户ID无效";
            return false;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            error = "必须填写修改原因";
            return false;
        }

        if (string.IsNullOrWhiteSpace(operatorName))
        {
            error = "必须填写修改人";
            return false;
        }

        var current = GetUser(userId)?.Points ?? 0;
        var delta = points - current;
        if (delta == 0)
        {
            return true;
        }

        var ledgerReason = $"[{operatorName}] {reason}";
        return TryChangePoints(
            userId, "", delta, PointsTransactionType.AdminSet, ledgerReason, null, out _, operatorName);
    }

    public bool TryAdminAdjustPoints(string userId, int delta, string reason, string operatorName, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(userId) || delta == 0)
        {
            error = delta == 0 ? null : "用户ID无效";
            return delta == 0;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            error = "必须填写修改原因";
            return false;
        }

        if (string.IsNullOrWhiteSpace(operatorName))
        {
            error = "必须填写修改人";
            return false;
        }

        if (delta < 0)
        {
            var current = GetUser(userId)?.Points ?? 0;
            if (current + delta < 0)
            {
                delta = -current;
            }
        }

        if (delta == 0)
        {
            return true;
        }

        var ledgerReason = $"[{operatorName}] {reason}";
        return TryChangePoints(
            userId, "", delta, PointsTransactionType.AdminAdjust, ledgerReason, null, out _, operatorName);
    }

    [Obsolete("Use TryAdminSetPoints with reason and operator")]
    public void SetPoints(string userId, int points)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var current = GetUser(userId)?.Points ?? 0;
        var delta = points - current;
        if (delta == 0)
        {
            return;
        }

        TryChangePoints(userId, "", delta, PointsTransactionType.AdminSet, "管理员设置积分", null, out _);
    }

    [Obsolete("Use TryAdminAdjustPoints with reason and operator")]
    public void AdjustPoints(string userId, int delta)
    {
        if (string.IsNullOrWhiteSpace(userId) || delta == 0)
        {
            return;
        }

        if (delta < 0)
        {
            var current = GetUser(userId)?.Points ?? 0;
            if (current + delta < 0)
            {
                delta = -current;
            }
        }

        if (delta == 0)
        {
            return;
        }

        TryChangePoints(userId, "", delta, PointsTransactionType.AdminAdjust, "管理员调整积分", null, out _);
    }

    public int CountRequestsToday(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return 0;
        }

        var today = DateTime.Today.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM queue_items
            WHERE user_id = $uid AND is_random = 0 AND created_at >= $today
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$today", today);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void TouchInteraction(string userId, string nickname)
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
            SET nickname = CASE WHEN length($nick) > 0 THEN $nick ELSE nickname END,
                updated_at = $now
            WHERE user_id = $uid
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$nick", nickname);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 原子更新积分并写入流水。扣减时使用 SQL 条件保证余额充足。
    /// </summary>
    public bool TryChangePoints(
        string userId,
        string nickname,
        int delta,
        string type,
        string reason,
        string? refId,
        out int balanceAfter,
        string? operatorName = null)
    {
        balanceAfter = 0;
        if (string.IsNullOrWhiteSpace(userId) || delta == 0)
        {
            return false;
        }

        EnsureUser(userId, nickname);
        var now = DateTime.Now.ToString("O");

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            int rows;
            if (delta < 0)
            {
                var deduct = -delta;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        UPDATE users
                        SET nickname = CASE WHEN length($nick) > 0 THEN $nick ELSE nickname END,
                            points = points - $pts,
                            updated_at = $now
                        WHERE user_id = $uid AND points >= $pts
                        """;
                    cmd.Parameters.AddWithValue("$uid", userId);
                    cmd.Parameters.AddWithValue("$nick", nickname);
                    cmd.Parameters.AddWithValue("$pts", deduct);
                    cmd.Parameters.AddWithValue("$now", now);
                    rows = cmd.ExecuteNonQuery();
                }

                if (rows == 0)
                {
                    tx.Rollback();
                    return false;
                }
            }
            else
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        UPDATE users
                        SET nickname = CASE WHEN length($nick) > 0 THEN $nick ELSE nickname END,
                            points = points + $pts,
                            updated_at = $now
                        WHERE user_id = $uid
                        """;
                    cmd.Parameters.AddWithValue("$uid", userId);
                    cmd.Parameters.AddWithValue("$nick", nickname);
                    cmd.Parameters.AddWithValue("$pts", delta);
                    cmd.Parameters.AddWithValue("$now", now);
                    rows = cmd.ExecuteNonQuery();
                }

                if (rows == 0)
                {
                    tx.Rollback();
                    return false;
                }
            }

            using (var readCmd = conn.CreateCommand())
            {
                readCmd.Transaction = tx;
                readCmd.CommandText = "SELECT points FROM users WHERE user_id = $uid";
                readCmd.Parameters.AddWithValue("$uid", userId);
                balanceAfter = Convert.ToInt32(readCmd.ExecuteScalar() ?? 0);
            }

            _ledger.Insert(conn, tx, userId, delta, balanceAfter, type, reason, refId, operatorName: operatorName);
            tx.Commit();
            return true;
        }
        catch
        {
            try
            {
                tx.Rollback();
            }
            catch
            {
                // ignore rollback errors
            }

            throw;
        }
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

        EnsureUser(userId, "");
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

        EnsureUser(userId, "");
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

        DateTime? lastInteraction = null;
        if (reader.FieldCount > 10 && !reader.IsDBNull(10) && DateTime.TryParse(reader.GetString(10), out var updated))
        {
            lastInteraction = updated;
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
            SongPermissionUnlimited = reader.FieldCount > 9 && !reader.IsDBNull(9) && reader.GetInt64(9) == 1,
            LastInteractionAt = lastInteraction
        };
    }
}
