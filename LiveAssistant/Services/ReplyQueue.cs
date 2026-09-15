using System.Threading.Channels;
using LiveAssistant.Config;
using LiveAssistant.Models;

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
    private readonly OutboundReplyTracker? _outboundTracker;
    private readonly Func<string, string, string, CancellationToken, Task<bool>> _sendMention;
    private readonly bool _sendMentionInjected;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly Queue<DateTime> _sentTimestamps = new();
    private readonly object _rateLock = new();
    private readonly object _batchLock = new();
    private readonly List<SongRequestReplyEntry> _songBatch = new();
    private string _batchWebRid = "";
    private CancellationTokenSource? _batchCts;

    public ReplyQueue(
        DouyinService douyin,
        LogService log,
        ReplySettings settings,
        ReplyIdempotencyStore? idempotency = null,
        Func<string, string, string, CancellationToken, Task<bool>>? sendMention = null,
        OutboundReplyTracker? outboundTracker = null)
    {
        _douyin = douyin;
        _log = log;
        _settings = settings;
        _idempotency = idempotency ?? new ReplyIdempotencyStore();
        _outboundTracker = outboundTracker;
        _sendMentionInjected = sendMention != null;
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
            return;
        }

        _outboundTracker?.Track(job.ReplyId, job.Content);
    }

    public void EnqueueSongRequestReply(string webRid, string userId, string nickname, string songName, int aheadCount)
    {
        if (string.IsNullOrWhiteSpace(webRid))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            _log.DouyinWarn($"点歌回复跳过：缺少 user_id，无法 @ {nickname}");
            return;
        }

        lock (_batchLock)
        {
            _batchWebRid = webRid;
            _songBatch.Add(new SongRequestReplyEntry
            {
                UserId = userId,
                Nickname = nickname,
                SongName = songName,
                AheadCount = Math.Max(0, aheadCount)
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

        lock (_batchLock)
        {
            if (_songBatch.Count == 0)
            {
                return;
            }

            items = _songBatch.ToList();
            webRid = _batchWebRid;
            _songBatch.Clear();
        }

        foreach (var group in items.GroupBy(e => e.UserId, StringComparer.Ordinal))
        {
            var userId = group.Key;
            if (string.IsNullOrWhiteSpace(userId))
            {
                _log.DouyinWarn("点歌回复跳过：缺少 user_id，无法 @ 对方");
                continue;
            }

            var content = SongRequestReplyFormatter.FormatForUser(group.ToList());
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            TryEnqueue(new ReplyJob
            {
                ReplyId = NewReplyId(),
                WebRid = webRid,
                UserId = userId,
                Content = content,
                IsSongRequestBatch = group.Count() > 1
            });
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
                    var sentAt = DateTime.UtcNow;
                    var detail = await SendMentionWithDiagnosticAsync(job, ct);
                    LogReplySendDiagnostic(job, detail, sentAt);
                    if (detail.Ok)
                    {
                        _idempotency.MarkSucceeded(job.ReplyId);
                        RecordSent();
                        _log.DouyinInfo(
                            $"[reply-send] reply_id={job.ReplyId} target_user={job.UserId} " +
                            $"content={Truncate(job.Content)} result=ok batch={job.IsSongRequestBatch}");
                        continue;
                    }

                    await HandleFailureAsync(job, detail.ErrorReason, ct);
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

    private async Task<MentionSendResult> SendMentionWithDiagnosticAsync(ReplyJob job, CancellationToken ct)
    {
        if (_sendMentionInjected)
        {
            var ok = await _sendMention(job.WebRid, job.UserId, job.Content, ct);
            return new MentionSendResult
            {
                Ok = ok,
                HttpStatus = ok ? 200 : 400,
                ErrorReason = ok ? "" : "发送返回失败",
                ReplyType = job.IsSongRequestBatch ? "song_request_batch" : "mention"
            };
        }

        return await _douyin.SendMentionDetailedAsync(job.WebRid, job.UserId, job.Content, ct);
    }

    private void LogReplySendDiagnostic(ReplyJob job, MentionSendResult detail, DateTime sentAtUtc)
    {
        var type = job.IsSongRequestBatch ? "song_request_batch" : "mention";
        _log.DouyinInfo(
            $"REPLY_SEND_DIAGNOSTIC replyId={job.ReplyId} type={type} " +
            $"httpStatus={detail.HttpStatus?.ToString() ?? "-"} ok={detail.Ok} " +
            $"requestTime={sentAtUtc:O} error={detail.ErrorReason} retry={job.RetryCount}");
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

        // 侧车已发出弹幕但回显格式与发送内容不完全一致（常见：去掉换行）——勿重试
        if (IsLikelyAlreadySent(reason))
        {
            _idempotency.MarkSucceeded(job.ReplyId);
            RecordSent();
            _log.DouyinInfo(
                $"[reply-send] reply_id={job.ReplyId} target_user={job.UserId} " +
                $"content={Truncate(job.Content)} result=ok_likely_sent error={reason}");
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

    private static bool IsLikelyAlreadySent(string reason)
        => !string.IsNullOrWhiteSpace(reason)
           && (reason.Contains("假成功", StringComparison.Ordinal)
               || reason.Contains("内容不一致", StringComparison.Ordinal));

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
