using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 电影评分事件异步上传；失败退避重试，不阻塞本地评分/弹幕/点歌。
/// </summary>
public sealed class MovieScoreSyncService : IDisposable
{
    private static readonly int[] BackoffSeconds = { 5, 15, 30, 60 };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConfigManager _config;
    private readonly MovieInteractionService _movie;
    private readonly LogService _log;
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private int _disposed;

    public MovieScoreSyncService(ConfigManager config, MovieInteractionService movie, LogService log, HttpMessageHandler? handler = null)
    {
        _config = config;
        _movie = movie;
        _log = log;
        _http = handler == null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }
            : new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
        _log.Info("[MOVIE_SCORE_SYNC] MovieScoreSyncService 已启动");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                await FlushOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("movie_score", "[MOVIE_SCORE_SYNC] 同步循环异常", ex);
            }
        }
    }

    public async Task FlushOnceAsync(CancellationToken ct = default)
    {
        var settings = _config.Settings.MovieInteraction;
        if (!settings.Enabled || !settings.SyncEnabled)
        {
            return;
        }

        var baseUrl = (settings.ServerBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        var pending = _movie.Repository.GetPendingSyncEvents(DateTime.Now);
        foreach (var ev in pending)
        {
            ct.ThrowIfCancellationRequested();
            await UploadOneAsync(baseUrl, settings.ApiToken, ev, ct).ConfigureAwait(false);
        }
    }

    private async Task UploadOneAsync(string baseUrl, string? apiToken, MovieScoreEvent ev, CancellationToken ct)
    {
        var url = $"{baseUrl}/api/movie-score/events";
        var body = new
        {
            eventId = ev.EventId,
            platform = ev.Platform,
            roomId = ev.RoomId,
            userId = ev.UserId,
            nickname = ev.Nickname,
            movieId = ev.MovieId,
            movieName = ev.MovieName,
            action = ev.Action,
            scoreDelta = ev.ScoreDelta,
            absolutePoints = ev.AbsolutePoints,
            sourceGiftEventIds = ev.SourceGiftEventIds,
            createdAt = ev.CreatedAt.ToString("O")
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(apiToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
            }

            req.Content = JsonContent.Create(body, options: JsonOptions);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                _movie.Repository.MarkUploadSuccess(ev.EventId, DateTime.Now);
                _log.Info($"[MOVIE_SCORE_SYNC] 上传成功 eventId={ev.EventId} http={code} attempts={ev.UploadAttempts}");
                return;
            }

            ScheduleRetry(ev, code, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ScheduleRetry(ev, 0, ex.Message);
        }
    }

    private void ScheduleRetry(MovieScoreEvent ev, int httpStatus, string? error)
    {
        var attempts = ev.UploadAttempts + 1;
        var delayIdx = Math.Min(attempts - 1, BackoffSeconds.Length - 1);
        var delaySec = BackoffSeconds[Math.Max(0, delayIdx)];
        var next = DateTime.Now.AddSeconds(delaySec);
        _movie.Repository.MarkUploadFailure(ev.EventId, attempts, next);
        _log.Info(
            $"[MOVIE_SCORE_SYNC] 上传失败 eventId={ev.EventId} http={httpStatus} " +
            $"attempts={attempts} nextRetryIn={delaySec}s err={Truncate(error, 120)}");
    }

    private static string Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : text[..max] + "…";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        try { _http.Dispose(); } catch { /* ignore */ }
    }
}
