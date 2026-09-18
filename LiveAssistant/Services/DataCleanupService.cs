using LiveAssistant.Config;
using LiveAssistant.Database;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Services;

public sealed class DataCleanupService : IDisposable
{
    private readonly ConfigManager _config;
    private readonly AppDatabase _db;
    private readonly LogService _log;
    private CancellationTokenSource? _cts;

    public DataCleanupService(ConfigManager config, AppDatabase db, LogService log)
    {
        _config = config;
        _db = db;
        _log = log;
    }

    public void Start()
    {
        if (!_config.Settings.Cleanup.Enabled)
        {
            return;
        }

        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                RunCleanup();
            }
            catch (Exception ex)
            {
                _log.Error("cleanup", "数据清理异常", ex);
            }

            try
            {
                var hours = Math.Max(1, _config.Settings.Cleanup.IntervalHours);
                await Task.Delay(TimeSpan.FromHours(hours), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void RunCleanup()
    {
        var settings = _config.Settings.Cleanup;
        var now = DateTime.Now;
        var queueCutoff = now.AddDays(-settings.QueueRetentionDays).ToString("O");
        var giftCutoff = now.AddDays(-settings.GiftRetentionDays).ToString("O");

        using var conn = _db.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM queue_items
                WHERE status IN ('finished', 'deleted') AND updated_at < $cutoff
                """;
            cmd.Parameters.AddWithValue("$cutoff", queueCutoff);
            var n = cmd.ExecuteNonQuery();
            if (n > 0) _log.AdminInfo($"清理队列记录 {n} 条");
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM gift_events WHERE created_at < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", giftCutoff);
            var n = cmd.ExecuteNonQuery();
            if (n > 0) _log.AdminInfo($"清理礼物记录 {n} 条");
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM admin_commands
                WHERE status = 'processed' AND processed_at < $cutoff
                """;
            cmd.Parameters.AddWithValue("$cutoff", queueCutoff);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM ban_vote_sessions
                WHERE status IN ('completed', 'expired') AND completed_at < $cutoff
                """;
            cmd.Parameters.AddWithValue("$cutoff", queueCutoff);
            cmd.ExecuteNonQuery();
        }

        var streamDays = Math.Max(7, settings.MovieInteractionStreamRetentionDays);
        var streamCutoff = now.AddDays(-streamDays);
        try
        {
            var streamRepo = new MovieInteractionRepository(_db);
            var n = streamRepo.CleanupOldStream(streamCutoff);
            if (n > 0) _log.AdminInfo($"清理电影互动事件流 {n} 条（保留≥{streamDays}天）");
        }
        catch (Exception ex)
        {
            _log.Error("cleanup", "电影互动事件流清理失败", ex);
        }
    }

    public void Dispose() => Stop();
}
