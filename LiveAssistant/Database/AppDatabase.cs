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

        using (var idxCmd = conn.CreateCommand())
        {
            idxCmd.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_gift_events_event_id
                ON gift_events(event_id) WHERE event_id IS NOT NULL AND event_id != ''
                """;
            idxCmd.ExecuteNonQuery();
        }

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

    public int RecoverPlayingQueueItems()
    {
        using var conn = Open();
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(1) FROM queue_items WHERE status = 'playing'";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());
        if (count <= 0)
        {
            return 0;
        }

        using var resetCmd = conn.CreateCommand();
        resetCmd.CommandText = """
            UPDATE queue_items
            SET status = 'waiting', updated_at = $updated
            WHERE status = 'playing'
            """;
        resetCmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
        resetCmd.ExecuteNonQuery();
        return count;
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
        return conn;
    }

    public void Dispose()
    {
    }
}
