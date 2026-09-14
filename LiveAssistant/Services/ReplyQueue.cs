using System.Threading.Channels;
using LiveAssistant.Config;

namespace LiveAssistant.Services;

public sealed class ReplyJob
{
    public required string ReplyId { get; init; }
    public required string WebRid { get; init; }
    public required string UserId { get; init; }
    public required string Content { get; init; }
    public int RetryCount { get; set; }
    public bool IsSongRequestBatch { get; init; }
}

/// <summary>
/// 弹幕回复统一发送队列：限速、防刷屏、失败重试、reply_id 幂等。
/// </summary>
public sealed class ReplyQueue : IDisposable
{
    private readonly Channel<ReplyJob> _channel =
        Channel.CreateUnbounded<ReplyJob>(new UnboundedChannelOptions { SingleReader = true });

    private readonly DouyinService _douyin;
    private readonly LogService _log;
    private readonly ReplySettings _settings;
    private readonly ReplyIdempotencyStore _idempotency;
    private readonly Func<string, string, string, CancellationToken, Task<bool>> _sendMention;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly Queue<DateTime> _sentTimestamps = new();
    private readonly object _rateLock = new();
    private readonly object _batchLock = new();
    private readonly List<SongRequestReplyEntry> _songBatch = new();
    private string _batchWebRid = "";
    private string _batchUserId = "";
    private int _batchQueueCount;
    private CancellationTokenSource? _batchCts;

    public ReplyQueue(
        DouyinService douyin,
        LogService log,
        ReplySettings settings,
        ReplyIdempotencyStore? idempotency = null,
        Func<string, string, string, CancellationToken, Task<bool>>? sendMention = null)
    {
        _douyin = douyin;
        _log = log;
        _settings = settings;
        _idempotency = idempotency ?? new ReplyIdempotencyStore();
        _sendMention = sendMention ?? ((webRid, userId, content, ct) =>
            _douyin.SendMentionAsync(webRid, userId, content, ct));
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public ReplyIdempotencyStore Idempotency => _idempotency;

    public void EnqueueMention(string webRid, string userId, string content)
    {
        if (string.IsNullOrWhiteSpace(webRid) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        TryEnqueue(new ReplyJob
        {
            ReplyId = NewReplyId(),
            WebRid = webRid,
            UserId = userId,
            Content = content
        });
    }

    /// <summary>测试/内部：按指定 reply_id 入队（用于幂等验证）。</summary>
    internal void EnqueueMentionWithReplyId(string replyId, string webRid, string userId, string content)
    {
        TryEnqueue(new ReplyJob
        {
            ReplyId = replyId,
            WebRid = webRid,
            UserId = userId,
            Content = content
        });
    }

    private void TryEnqueue(ReplyJob job)
    {
        if (!_channel.Writer.TryWrite(job))
        {
            _log.DouyinWarn($"回复入队失败 reply_id={job.ReplyId}");
        }
    }

    public void EnqueueSongRequestReply(string webRid, string userId, string nickname, string songName, int queueCount)
    {
        if (string.IsNullOrWhiteSpace(webRid) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        lock (_batchLock)
        {
            _batchWebRid = webRid;
            _batchUserId = userId;
            _batchQueueCount = queueCount;
            _songBatch.Add(new SongRequestReplyEntry
            {
                Nickname = nickname,
                SongName = songName
            });

            _batchCts?.Cancel();
            _batchCts = new CancellationTokenSource();
            var token = _batchCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_settings.SongRequestBatchWindowMs, token);
                    FlushSongRequestBatch();
                }
                catch (OperationCanceledException)
                {
                    // replaced by newer batch window
                }
            }, token);
        }
    }

    internal void FlushSongRequestBatch()
    {
        List<SongRequestReplyEntry> items;
        string webRid;
        string userId;
        int queueCount;

        lock (_batchLock)
        {
            if (_songBatch.Count == 0)
            {
                return;
            }

            items = _songBatch.ToList();
            webRid = _batchWebRid;
            userId = _batchUserId;
            queueCount = _batchQueueCount;
            _songBatch.Clear();
        }

        var content = SongRequestReplyFormatter.FormatMerged(items, queueCount);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var job = new ReplyJob
        {
            ReplyId = NewReplyId(),
            WebRid = webRid,
            UserId = userId,
            Content = content,
            IsSongRequestBatch = true
        };

        if (!_channel.Writer.TryWrite(job))
        {
            _log.DouyinWarn($"点歌合并回复入队失败 reply_id={job.ReplyId}");
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    if (_idempotency.HasSucceeded(job.ReplyId))
                    {
                        _log.DouyinInfo(
                            $"[reply-send] reply_id={job.ReplyId} target_user={job.UserId} " +
                            $"content={Truncate(job.Content)} result=skip_already_sent");
                        continue;
                    }

                    await WaitForRateLimitAsync(ct);
                    var ok = await _sendMention(job.WebRid, job.UserId, job.Content, ct);
                    if (ok)
                    {
                        _idempotency.MarkSucceeded(job.ReplyId);
                        RecordSent();
                        _log.DouyinInfo(
                            $"[reply-send] reply_id={job.ReplyId} target_user={job.UserId} " +
                            $"content={Truncate(job.Content)} result=ok batch={job.IsSongRequestBatch}");
                        continue;
                    }

                    await HandleFailureAsync(job, "发送返回失败", ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await HandleFailureAsync(job, ex.Message, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _log.Error("douyin", "回复队列异常退出", ex);
        }
    }

    private async Task HandleFailureAsync(ReplyJob job, string reason, CancellationToken ct)
    {
        // 若已成功发送过（例如先前成功后被重复入队），不再重试
        if (_idempotency.HasSucceeded(job.ReplyId))
        {
            _log.DouyinInfo(
                $"[reply-send] reply_id={job.ReplyId} target_user={job.UserId} " +
                $"content={Truncate(job.Content)} result=skip_already_sent");
            return;
        }

        job.RetryCount++;
        if (job.RetryCount <= _settings.MaxRetries)
        {
            _log.DouyinWarn(
                $"[reply-send] reply_id={job.ReplyId} target_user={job.UserId} " +
                $"content={Truncate(job.Content)} result=retry/{job.RetryCount} error={reason}");
            try
            {
                await Task.Delay(_settings.RetryDelayMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _channel.Writer.TryWrite(job);
            return;
        }

        _log.Error(
            "douyin",
            $"[reply-send] reply_id={job.ReplyId} target_user={job.UserId} " +
            $"content={Truncate(job.Content)} result=fail error={reason}");
    }

    private async Task WaitForRateLimitAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_rateLock)
            {
                var now = DateTime.UtcNow;
                while (_sentTimestamps.Count > 0 && (now - _sentTimestamps.Peek()).TotalSeconds >= 1)
                {
                    _sentTimestamps.Dequeue();
                }

                if (_sentTimestamps.Count < _settings.MaxPerSecond)
                {
                    return;
                }
            }

            await Task.Delay(200, ct);
        }
    }

    private void RecordSent()
    {
        lock (_rateLock)
        {
            _sentTimestamps.Enqueue(DateTime.UtcNow);
        }
    }

    private static string NewReplyId() => Guid.NewGuid().ToString("N");

    private static string Truncate(string s, int max = 120)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
        {
            return s;
        }

        return s[..max] + "...";
    }

    public void Dispose()
    {
        FlushSongRequestBatch();
        _batchCts?.Cancel();
        _cts.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }
        _cts.Dispose();
        _batchCts?.Dispose();
    }
}
