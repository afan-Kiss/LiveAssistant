using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

/// <summary>
/// 将历史 data 目录中的电影评分合并到当前统一数据目录，避免 Debug/Publish 多套库导致重启丢分。
/// </summary>
internal static class LegacyMovieScoreImporter
{
    private static readonly string[] ScoreTables = ["movie_score_events", "movie_score_credits"];

    public static int TryImport(string currentDataDir)
    {
        var currentDb = Path.Combine(currentDataDir, "liveassistant.db");
        if (!File.Exists(currentDb))
        {
            return 0;
        }

        var imported = 0;
        using var conn = new SqliteConnection($"Data Source={currentDb}");
        conn.Open();

        foreach (var legacyDb in CollectCandidates(currentDataDir))
        {
            try
            {
                imported += MergeFrom(conn, legacyDb);
            }
            catch (SqliteException)
            {
                // 旧库缺表或结构不兼容时跳过，不得阻断启动
            }
        }

        if (imported > 0)
        {
            RecalculateTotals(conn);
        }

        return imported;
    }

    private static IEnumerable<string> CollectCandidates(string currentDataDir)
    {
        var current = Path.GetFullPath(currentDataDir);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                return;
            }

            var dir = Path.GetDirectoryName(full);
            if (string.IsNullOrWhiteSpace(dir)
                || string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(full))
            {
                return;
            }

            list.Add(full);
        }

        Add(Path.Combine(AppPaths.ExeDirectory, "data", "liveassistant.db"));

        var sourceRoot = Environment.GetEnvironmentVariable("LIVE_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            var repo = Path.Combine(sourceRoot, "抖音弹幕点歌系统");
            Add(Path.Combine(repo, "publish", "LiveAssistant-one", "data", "liveassistant.db"));
            Add(Path.Combine(repo, "LiveAssistant", "bin", "Debug", "net8.0-windows", "data", "liveassistant.db"));
            Add(Path.Combine(repo, "LiveAssistant", "bin", "Release", "net8.0-windows", "data", "liveassistant.db"));
            Add(Path.Combine(repo, "LiveAssistant", "bin", "Release", "net8.0-windows", "win-x64", "data", "liveassistant.db"));
        }

        return list;
    }

    private static int MergeFrom(SqliteConnection target, string legacyDbPath)
    {
        if (!HasScoreTables(legacyDbPath))
        {
            return 0;
        }

        var alias = "legacy_" + Guid.NewGuid().ToString("N");
        using var attach = target.CreateCommand();
        attach.CommandText = $"ATTACH DATABASE $path AS {alias}";
        attach.Parameters.AddWithValue("$path", legacyDbPath);
        attach.ExecuteNonQuery();

        try
        {
            var imported = 0;
            foreach (var table in ScoreTables)
            {
                imported += CopyIgnore(target, alias, table);
            }

            return imported;
        }
        finally
        {
            using var detach = target.CreateCommand();
            detach.CommandText = $"DETACH DATABASE {alias}";
            detach.ExecuteNonQuery();
        }
    }

    private static bool HasScoreTables(string legacyDbPath)
    {
        using var conn = new SqliteConnection($"Data Source={legacyDbPath}");
        conn.Open();
        foreach (var table in ScoreTables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(1) FROM sqlite_master
                WHERE type = 'table' AND name = $name
                """;
            cmd.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int CopyIgnore(SqliteConnection conn, string alias, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT OR IGNORE INTO main.{table} SELECT * FROM {alias}.{table}";
        return cmd.ExecuteNonQuery();
    }

    private static void RecalculateTotals(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();

        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM movie_score_totals";
            clear.ExecuteNonQuery();
        }

        using (var rebuild = conn.CreateCommand())
        {
            rebuild.Transaction = tx;
            rebuild.CommandText = """
                INSERT INTO movie_score_totals (movie_id, movie_name, score, updated_at)
                SELECT e.movie_id,
                       COALESCE(NULLIF(MAX(e.movie_name), ''), e.movie_id) AS movie_name,
                       COALESCE(SUM(e.score_delta), 0) AS score,
                       COALESCE(MAX(e.created_at), datetime('now')) AS updated_at
                FROM movie_score_events e
                GROUP BY e.movie_id
                HAVING COALESCE(SUM(e.score_delta), 0) <> 0
                """;
            rebuild.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
