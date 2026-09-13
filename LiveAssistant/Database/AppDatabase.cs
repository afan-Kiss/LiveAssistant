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
                created_at TEXT
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

    public SqliteConnection Open() => new(_connectionString);

    public void Dispose()
    {
    }
}
