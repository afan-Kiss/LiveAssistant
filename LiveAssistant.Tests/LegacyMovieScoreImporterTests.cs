using LiveAssistant;
using LiveAssistant.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LiveAssistant.Tests;

public sealed class LegacyMovieScoreImporterTests
{
    [Fact]
    public void TryImport_MergesLegacyEventsAndRebuildsTotals()
    {
        var root = Path.Combine(Path.GetTempPath(), "la-legacy-" + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "current");
        var legacyExe = Path.Combine(root, "legacy-exe");
        var legacyData = Path.Combine(legacyExe, "data");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(legacyData);

        try
        {
            Environment.SetEnvironmentVariable("LIVE_SOURCE_ROOT", null);
            using (var legacyDb = new AppDatabase(legacyData))
            {
                InsertEvent(legacyDb, "e1", "1462628", "哪吒", 10);
            }

            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", legacyExe);
            ResetAppPathsCache();

            using (var currentDb = new AppDatabase(current))
            {
                InsertEvent(currentDb, "e2", "2", "流浪地球", -20);
            }

            using var conn = new SqliteConnection($"Data Source={Path.Combine(current, "liveassistant.db")}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT movie_id, score FROM movie_score_totals ORDER BY movie_id";
            using var reader = cmd.ExecuteReader();
            var rows = new Dictionary<string, long>();
            while (reader.Read())
            {
                rows[reader.GetString(0)] = reader.GetInt64(1);
            }

            Assert.Equal(10, rows["1462628"]);
            Assert.Equal(-20, rows["2"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LA_TEST_EXE_DIR", null);
            Environment.SetEnvironmentVariable("LIVE_SOURCE_ROOT", null);
            ResetAppPathsCache();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // Windows 上 SQLite 文件锁可能延迟释放，忽略清理失败
            }
        }
    }

    private static void InsertEvent(AppDatabase db, string eventId, string movieId, string movieName, int delta)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO movie_score_events (
                    event_id, platform, room_id, user_id, nickname,
                    movie_id, movie_name, action, score_delta, absolute_points,
                    source_gift_event_ids, created_at, upload_status, upload_attempts)
                VALUES (
                    $eid, 'douyin', '', 'u1', 'tester',
                    $mid, $mname, 'good', $delta, $delta,
                    '[]', $created, 'pending', 0)
                """;
            insert.Parameters.AddWithValue("$eid", eventId);
            insert.Parameters.AddWithValue("$mid", movieId);
            insert.Parameters.AddWithValue("$mname", movieName);
            insert.Parameters.AddWithValue("$delta", delta);
            insert.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        using (var total = conn.CreateCommand())
        {
            total.Transaction = tx;
            total.CommandText = """
                INSERT INTO movie_score_totals (movie_id, movie_name, score, updated_at)
                VALUES ($mid, $mname, $delta, $created)
                """;
            total.Parameters.AddWithValue("$mid", movieId);
            total.Parameters.AddWithValue("$mname", movieName);
            total.Parameters.AddWithValue("$delta", delta);
            total.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            total.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void ResetAppPathsCache()
    {
        var exeField = typeof(AppPaths).GetField("_cachedExeDirectory",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        exeField?.SetValue(null, null);
        var dataField = typeof(AppPaths).GetField("_cachedDataDirectory",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        dataField?.SetValue(null, null);
    }
}
