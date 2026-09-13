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
            """;
        cmd.ExecuteNonQuery();
    }

    private void Migrate()
    {
        using var conn = Open();
        EnsureColumn(conn, "queue_items", "status", "TEXT DEFAULT 'waiting'");
        EnsureColumn(conn, "queue_items", "updated_at", "TEXT");

        using var resetCmd = conn.CreateCommand();
        resetCmd.CommandText = """
            UPDATE queue_items SET status = 'waiting'
            WHERE status = 'playing' OR status IS NULL OR status = '';
            """;
        resetCmd.ExecuteNonQuery();

        using var legacyCmd = conn.CreateCommand();
        legacyCmd.CommandText = """
            UPDATE queue_items SET status = 'waiting'
            WHERE status NOT IN ('waiting', 'playing', 'finished', 'deleted');
            """;
        legacyCmd.ExecuteNonQuery();
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

    public SqliteConnection Open() => new(_connectionString);

    public void Dispose()
    {
    }
}
