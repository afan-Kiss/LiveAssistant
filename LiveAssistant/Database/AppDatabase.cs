using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class AppDatabase : IDisposable
{
    private readonly string _connectionString;

    public AppDatabase(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "liveassistant.db");
        _connectionString = $"Data Source={dbPath}";
        Initialize();
        Migrate();
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                user_id TEXT PRIMARY KEY,
                nickname TEXT NOT NULL,
                role TEXT DEFAULT 'normal',
                status TEXT DEFAULT 'active',
                points INTEGER DEFAULT 0,
                level INTEGER DEFAULT 0,
                request_count INTEGER DEFAULT 0,
                last_request_at TEXT,
                created_at TEXT,
                updated_at TEXT
            );

            CREATE TABLE IF NOT EXISTS song_requests (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id TEXT,
                nickname TEXT,
                song_name TEXT,
                song_id TEXT,
                hash TEXT,
                status TEXT,
                created_at TEXT
            );

            CREATE TABLE IF NOT EXISTS queue_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id TEXT,
                nickname TEXT,
                song_name TEXT,
                artist TEXT,
                song_id TEXT,
                hash TEXT,
                is_random INTEGER DEFAULT 0,
                sort_order INTEGER DEFAULT 0,
                status TEXT DEFAULT 'waiting',
                created_at TEXT,
                updated_at TEXT
            );

            CREATE TABLE IF NOT EXISTS random_play_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                song_id TEXT,
                hash TEXT,
                played_at TEXT
            );

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT
            );

            CREATE TABLE IF NOT EXISTS gift_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id TEXT,
                nickname TEXT,
                gift_id TEXT,
                gift_name TEXT,
                count INTEGER DEFAULT 1,
                value INTEGER DEFAULT 0,
                created_at TEXT
            );

            CREATE TABLE IF NOT EXISTS ban_vote_sessions (
                id TEXT PRIMARY KEY,
                target_user_id TEXT,
                target_nickname TEXT,
                vote_count INTEGER DEFAULT 0,
                required_votes INTEGER,
                status TEXT DEFAULT 'active',
                created_at TEXT,
                completed_at TEXT
            );

            CREATE TABLE IF NOT EXISTS ban_votes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT,
                target_user_id TEXT,
                target_nickname TEXT,
                voter_user_id TEXT,
                voter_nickname TEXT,
                created_at TEXT
            );

            CREATE TABLE IF NOT EXISTS song_blacklist (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                song_name TEXT,
                artist TEXT,
                song_id TEXT,
                reason TEXT,
                created_at TEXT
            );

            CREATE TABLE IF NOT EXISTS keyword_replies (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                keyword TEXT,
                template_key TEXT,
                enabled INTEGER DEFAULT 1,
                created_at TEXT
            );

            CREATE TABLE IF NOT EXISTS admin_commands (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                command_type TEXT,
                payload TEXT,
                status TEXT DEFAULT 'pending',
                created_at TEXT,
                processed_at TEXT
            );

            CREATE TABLE IF NOT EXISTS reply_templates (
                template_key TEXT PRIMARY KEY,
                content TEXT NOT NULL,
                updated_at TEXT
            );

            CREATE TABLE IF NOT EXISTS random_pool_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                song_name TEXT,
                artist TEXT,
                song_id TEXT,
                hash TEXT,
                keyword TEXT,
                enabled INTEGER DEFAULT 1,
                created_at TEXT
            );

            CREATE TABLE IF NOT EXISTS gift_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                gift_name TEXT,
                points INTEGER DEFAULT 0,
                enabled INTEGER DEFAULT 1,
                created_at TEXT
            );

            CREATE TABLE IF NOT EXISTS level_permissions (
                level INTEGER PRIMARY KEY,
                can_request INTEGER DEFAULT 1,
                cooldown_seconds INTEGER DEFAULT 30,
                min_points INTEGER DEFAULT 0,
                queue_priority INTEGER DEFAULT 0,
                points_cost_override INTEGER DEFAULT -1
            );

            CREATE TABLE IF NOT EXISTS welcome_records (
                user_id TEXT PRIMARY KEY,
                welcomed_at TEXT
            );

            CREATE TABLE IF NOT EXISTS points_ledger (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id TEXT NOT NULL,
                delta INTEGER NOT NULL,
                balance_after INTEGER NOT NULL,
                type TEXT NOT NULL,
                reason TEXT,
                ref_id TEXT,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS movie_score_credits (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                gift_event_id TEXT NOT NULL UNIQUE,
                user_id TEXT NOT NULL,
                nickname TEXT,
                gift_name TEXT,
                diamond_count INTEGER DEFAULT 0,
                value INTEGER DEFAULT 0,
                points INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed_at TEXT,
                score_event_id TEXT,
                status TEXT NOT NULL DEFAULT 'pending'
            );

            CREATE TABLE IF NOT EXISTS movie_score_events (
                event_id TEXT PRIMARY KEY,
                platform TEXT,
                room_id TEXT,
                user_id TEXT NOT NULL,
                nickname TEXT,
                movie_id TEXT NOT NULL,
                movie_name TEXT NOT NULL,
                action TEXT NOT NULL,
                score_delta INTEGER NOT NULL,
                absolute_points INTEGER NOT NULL,
                source_gift_event_ids TEXT,
                created_at TEXT NOT NULL,
                uploaded_at TEXT,
                upload_status TEXT NOT NULL DEFAULT 'pending',
                upload_attempts INTEGER DEFAULT 0,
                next_retry_at TEXT
            );

            CREATE TABLE IF NOT EXISTS movie_score_totals (
                movie_id TEXT PRIMARY KEY,
                movie_name TEXT,
                score INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT
            );

            CREATE TABLE IF NOT EXISTS movie_catalog (
                movie_id TEXT PRIMARY KEY,
                movie_name TEXT NOT NULL,
                aliases_json TEXT,
                rank INTEGER DEFAULT 0,
                updated_at TEXT
            );

            CREATE TABLE IF NOT EXISTS movie_interaction_stream (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                type TEXT NOT NULL,
                ref_id TEXT,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS song_request_charges (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                queue_item_id INTEGER NOT NULL UNIQUE,
                user_id TEXT NOT NULL,
                nickname TEXT,
                charge_type TEXT NOT NULL,
                points_deducted INTEGER NOT NULL DEFAULT 0,
                credit_consumed INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT 'charged',
                refund_reason TEXT,
                created_at TEXT NOT NULL,
                fulfilled_at TEXT,
                refunded_at TEXT
            );

            CREATE TABLE IF NOT EXISTS app_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private void Migrate()
    {
        using var conn = Open();
        EnsureColumn(conn, "users", "role", "TEXT DEFAULT 'normal'");
        EnsureColumn(conn, "users", "status", "TEXT DEFAULT 'active'");
        EnsureColumn(conn, "queue_items", "status", "TEXT DEFAULT 'waiting'");
        EnsureColumn(conn, "queue_items", "updated_at", "TEXT");
        EnsureColumn(conn, "ban_vote_sessions", "expires_at", "TEXT");
        EnsureColumn(conn, "ban_vote_sessions", "result", "TEXT");
        EnsureColumn(conn, "ban_vote_sessions", "initiator_user_id", "TEXT");
        EnsureColumn(conn, "ban_vote_sessions", "initiator_nickname", "TEXT");
        EnsureColumn(conn, "gift_events", "points_delta", "INTEGER DEFAULT 0");
        EnsureColumn(conn, "gift_events", "points_after", "INTEGER DEFAULT 0");
        EnsureColumn(conn, "gift_rules", "gift_id", "TEXT");
        EnsureColumn(conn, "gift_rules", "allow_song_request", "INTEGER DEFAULT 0");
        EnsureColumn(conn, "keyword_replies", "reply_content", "TEXT");
        EnsureColumn(conn, "level_permissions", "queue_priority", "INTEGER DEFAULT 0");
        EnsureColumn(conn, "level_permissions", "points_cost_override", "INTEGER DEFAULT -1");
        EnsureColumn(conn, "gift_events", "event_id", "TEXT");
        EnsureColumn(conn, "gift_rules", "song_permission_count", "INTEGER DEFAULT 0");
        EnsureColumn(conn, "users", "song_permission_credits", "INTEGER DEFAULT 0");
        EnsureColumn(conn, "users", "song_permission_unlimited", "INTEGER DEFAULT 0");
        EnsureColumn(conn, "points_ledger", "operator_name", "TEXT");

        using (var tableCmd = conn.CreateCommand())
        {
            tableCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS song_request_charges (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    queue_item_id INTEGER NOT NULL UNIQUE,
                    user_id TEXT NOT NULL,
                    nickname TEXT,
                    charge_type TEXT NOT NULL,
                    points_deducted INTEGER NOT NULL DEFAULT 0,
                    credit_consumed INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'charged',
                    refund_reason TEXT,
                    created_at TEXT NOT NULL,
                    fulfilled_at TEXT,
                    refunded_at TEXT
                );
                CREATE TABLE IF NOT EXISTS app_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            tableCmd.ExecuteNonQuery();
        }

        using (var idxCmd = conn.CreateCommand())
        {
            idxCmd.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_gift_events_event_id
                ON gift_events(event_id) WHERE event_id IS NOT NULL AND event_id != '';
                CREATE INDEX IF NOT EXISTS idx_points_ledger_user_created
                ON points_ledger(user_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_movie_score_credits_user_status
                ON movie_score_credits(user_id, status, expires_at);
                CREATE INDEX IF NOT EXISTS idx_movie_score_events_upload
                ON movie_score_events(upload_status, next_retry_at);
                CREATE INDEX IF NOT EXISTS idx_movie_interaction_stream_seq
                ON movie_interaction_stream(seq);
                CREATE INDEX IF NOT EXISTS idx_movie_score_events_movie_action_user
                ON movie_score_events(movie_id, action, user_id);
                CREATE INDEX IF NOT EXISTS idx_song_request_charges_status
                ON song_request_charges(status);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_song_request_charges_queue_item
                ON song_request_charges(queue_item_id);
                """;
            idxCmd.ExecuteNonQuery();
        }

        EnsureStreamEpoch(conn);

        using var roleCmd = conn.CreateCommand();
        roleCmd.CommandText = """
            UPDATE users SET role = 'normal'
            WHERE role IS NULL OR role = '';
            UPDATE users SET status = 'active'
            WHERE status IS NULL OR status = '';
            """;
        roleCmd.ExecuteNonQuery();

        using var legacyCmd = conn.CreateCommand();
        legacyCmd.CommandText = """
            UPDATE queue_items SET status = 'waiting'
            WHERE status NOT IN ('waiting', 'playing', 'finished', 'deleted');
            """;
        legacyCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 启动时放弃未播放的点歌队列，避免重启后从头重播历史点歌。
    /// 调用方应先对 charged 未 fulfilled 的点歌执行统一退款，再调用本方法。
    /// </summary>
    public int AbandonPendingQueueOnStartup()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE queue_items
            SET status = 'deleted', updated_at = $updated
            WHERE status IN ('waiting', 'playing')
            """;
        cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 列出启动时将被遗弃的 waiting/playing 队列项（非随机）。
    /// </summary>
    public List<(long Id, string UserId, bool IsRandom)> ListPendingQueueItemsForAbandon()
    {
        var list = new List<(long, string, bool)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, IFNULL(user_id, ''), IFNULL(is_random, 0)
            FROM queue_items
            WHERE status IN ('waiting', 'playing')
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0));
        }

        return list;
    }

    public string GetOrCreateStreamEpoch()
    {
        using var conn = Open();
        return EnsureStreamEpoch(conn);
    }

    public long GetStreamMaxSeq()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT IFNULL(MAX(seq), 0) FROM movie_interaction_stream";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static string EnsureStreamEpoch(SqliteConnection conn)
    {
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT value FROM app_meta WHERE key = 'movie_stream_epoch'";
            var existing = read.ExecuteScalar()?.ToString();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        var epoch = Guid.NewGuid().ToString("N");
        using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO app_meta (key, value) VALUES ('movie_stream_epoch', $v)
            """;
        insert.Parameters.AddWithValue("$v", epoch);
        insert.ExecuteNonQuery();

        using var read2 = conn.CreateCommand();
        read2.CommandText = "SELECT value FROM app_meta WHERE key = 'movie_stream_epoch'";
        return read2.ExecuteScalar()?.ToString() ?? epoch;
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA busy_timeout = 5000;
            PRAGMA journal_mode = WAL;
            """;
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void Dispose()
    {
    }
}
