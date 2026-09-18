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
    /// <summary>快手 @ 依赖昵称；抖音可空。</summary>
    public string? Nickname { get; init; }
    public int RetryCount { get; set; }
    public bool IsSongRequestBatch { get; init; }
    /// <summary>点歌相关结果不可因积压静默丢弃。</summary>
    public bool IsCritical { get; init; }
    public DateTime EnqueuedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 弹幕回复统一发送队列：限速、防刷屏、失败重试、reply_id 幂等。
/// </summary>
public sealed class ReplyQueue : IDisposable
{
    private const int MaxPendingJobs = 200;
    private static readonly TimeSpan NonCriticalMaxAge = TimeSpan.FromMinutes(2);

    private readonly Channel<ReplyJob> _channel =
        Channel.CreateUnbounded<ReplyJob>(new UnboundedChannelOptions { SingleReader = true });

    private readonly DouyinService _douyin;
    private readonly LogService _log;
    private readonly ReplySettings _settings;
    private readonly ReplyIdempotencyStore _idempotency;
    private readonly OutboundReplyTracker? _outboundTracker;
    private readonly Action<string>? _onSendFailed;
    private readonly Action<string>? _onSendSucceeded;
    private readonly Func<string, string, string, string?, CancellationToken, Task<MentionSendResult>> _sendMention;
    private readonly bool _sendMentionInjected;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly Queue<DateTime> _sentTimestamps = new();
    private readonly object _rateLock = new();
    private readonly object _batchLock = new();
    private readonly List<SongRequestReplyEntry> _songBatch = new();
    private string _batchWebRid = "";
    private CancellationTokenSource? _batchCts;
    private bool _batchTimerRunning;
    private DateTime _lastFailureNoticeUtc = DateTime.MinValue;
    private int _pendingDepth;
    private long _droppedCount;
    private long _expiredCount;

    public ReplyQueue(
        DouyinService douyin,
        LogService log,
        ReplySettings settings,
        ReplyIdempotencyStore? idempotency = null,
        Func<string, string, string, string?, CancellationToken, Task<MentionSendResult>>? sendMention = null,
        OutboundReplyTracker? outboundTracker = null,
        Action<string>? onSendFailed = null,
        Action<string>? onSendSucceeded = null)
    {
        _douyin = douyin;
        _log = log;
        _settings = settings;
        _idempotency = idempotency ?? new ReplyIdempotencyStore();
        _outboundTracker = outboundTracker;
        _onSendFailed = onSendFailed;
        _onSendSucceeded = onSendSucceeded;
        _sendMentionInjected = sendMention != null;
        _sendMention = sendMention ?? ((webRid, userId, content, nickname, ct) =>
            _douyin.SendMentionDetailedAsync(webRid, userId, content, nickname, ct));
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public ReplyIdempotencyStore Idempotency => _idempotency;
    internal int PendingDepth => Volatile.Read(ref _pendingDepth);
    internal long DroppedCount => Interlocked.Read(ref _droppedCount);
    internal long ExpiredCount => Interlocked.Read(ref _expiredCount);

    public void EnqueueMention(string webRid, string userId, string content, bool critical = false, string? nickname = null)
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
            Content = content,
            Nickname = nickname,
            IsCritical = critical,
            EnqueuedAtUtc = DateTime.UtcNow
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
            Content = content,
            EnqueuedAtUtc = DateTime.UtcNow
        });
    }

    private void TryEnqueue(ReplyJob job)
    {
        var depth = Volatile.Read(ref _pendingDepth);
        if (!job.IsCritical && !job.IsSongRequestBatch && depth >= MaxPendingJobs)
        {
            Interlocked.Increment(ref _droppedCount);
            _log.DouyinWarn(
                $"REPLY_QUEUE replyId={job.ReplyId} type=mention enqueueAt={job.EnqueuedAtUtc:O} " +
                $"result=dropped_overflow queueDepth={depth} droppedTotal={DroppedCount}");
            return;
        }

        if (!_channel.Writer.TryWrite(job))
        {
            _log.DouyinWarn($"回复入队失败 reply_id={job.ReplyId}");
            return;
        }

        var after = Interlocked.Increment(ref _pendingDepth);
        _log.DouyinInfo(
            $"REPLY_QUEUE replyId={job.ReplyId} type={(job.IsSongRequestBatch ? "song_request_batch" : "mention")} " +
            $"enqueueAt={job.EnqueuedAtUtc:O} retry={job.RetryCount} queueDepth={after}");
        // 注意：不在入队时 Track outbound，仅发送成功后再 Track
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
                AheadCount = Math.Max(0, aheadCount),
                WebRid = webRid
            });

            // 固定窗口：仅空批次启动一次计时，后续加入不得重置
            if (_batchTimerRunning)
            {
                return;
            }

            _batchTimerRunning = true;
            _batchCts?.Dispose();
            _batchCts = new CancellationTokenSource();
            var token = _batchCts.Token;
            var windowMs = Math.Max(100, _settings.SongRequestBatchWindowMs);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(windowMs, token);
                    FlushSongRequestBatch();
                }
                catch (OperationCanceledException)
                {
                    // disposed / shutdown
                }
            }, token);
        }
    }

    internal void FlushSongRequestBatch()
    {
        List<SongRequestReplyEntry> items;

        lock (_batchLock)
        {
            _batchTimerRunning = false;
            if (_songBatch.Count == 0)
            {
                return;
            }

            items = _songBatch.ToList();
            _songBatch.Clear();
        }

        foreach (var roomGroup in items.GroupBy(
                     e => string.IsNullOrWhiteSpace(e.WebRid) ? _batchWebRid : e.WebRid,
                     StringComparer.Ordinal))
        {
            var webRid = roomGroup.Key;
            foreach (var group in roomGroup.GroupBy(e => e.UserId, StringComparer.Ordinal))
            {
                var userId = group.Key;
                if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(webRid))
                {
                    _log.DouyinWarn("点歌回复跳过：缺少 user_id/webRid，无法 @ 对方");
                    continue;
                }

                var list = group.ToList();
                var content = SongRequestReplyFormatter.FormatForUser(list);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                TryEnqueue(new ReplyJob
                {
                    ReplyId = NewReplyId(),
                    WebRid = webRid,
                    UserId = userId,
                    Nickname = list[0].Nickname,
                    Content = content,
                    IsSongRequestBatch = list.Count > 1,
                    IsCritical = true,
                    EnqueuedAtUtc = DateTime.UtcNow
                });
            }
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(ct))
            {
                Interlocked.Decrement(ref _pendingDepth);
                try
                {
                    if (_idempotency.HasSucceeded(job.ReplyId))
                    {
                        _log.DouyinInfo(
                            $"[reply-send] reply_id={job.ReplyId} target_user={job.UserId} " +
                            $"content={Truncate(job.Content)} result=skip_already_sent");
                        continue;
                    }

                    if (!job.IsCritical && !job.IsSongRequestBatch
                        && DateTime.UtcNow - job.EnqueuedAtUtc > NonCriticalMaxAge)
                    {
                        Interlocked.Increment(ref _expiredCount);
                        _log.DouyinWarn(
                            $"REPLY_QUEUE replyId={job.ReplyId} type=mention enqueueAt={job.EnqueuedAtUtc:O} " +
                            $"result=expired ageMs={(DateTime.UtcNow - job.EnqueuedAtUtc).TotalMilliseconds:F0} " +
                            $"expiredTotal={ExpiredCount}");
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
                        _outboundTracker?.Track(job.ReplyId, job.Content, detail.PlatformMessageId, job.WebRid);
                        _log.DouyinInfo(
                            $"REPLY_QUEUE replyId={job.ReplyId} type={(job.IsSongRequestBatch ? "song_request_batch" : "mention")} " +
                            $"enqueueAt={job.EnqueuedAtUtc:O} sendAt={sentAt:O} retry={job.RetryCount} success=true " +
                            $"queueDepth={PendingDepth}");
                        _onSendSucceeded?.Invoke(job.Content);
                        continue;
                    }

                    await HandleFailureAsync(job, detail, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await HandleFailureAsync(job, new MentionSendResult
                    {
                        Ok = false,
                        ErrorReason = ex.Message,
                        ReplyType = job.IsSongRequestBatch ? "song_request_batch" : "mention"
                    }, ct);
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
            return await _sendMention(job.WebRid, job.UserId, job.Content, job.Nickname, ct);
        }

        return await _douyin.SendMentionDetailedAsync(job.WebRid, job.UserId, job.Content, job.Nickname, ct);
    }

    private void LogReplySendDiagnostic(ReplyJob job, MentionSendResult detail, DateTime sentAtUtc)
    {
        var type = job.IsSongRequestBatch ? "song_request_batch" : "mention";
        _log.DouyinInfo(
            $"REPLY_SEND_DIAGNOSTIC replyId={job.ReplyId} type={type} " +
            $"httpStatus={detail.HttpStatus?.ToString() ?? "-"} ok={detail.Ok} " +
            $"requestTime={sentAtUtc:O} error={detail.ErrorReason} retry={job.RetryCount}");
    }

    private async Task HandleFailureAsync(ReplyJob job, MentionSendResult detail, CancellationToken ct)
    {
        var reason = detail.ErrorReason;
        if (_idempotency.HasSucceeded(job.ReplyId))
        {
            _log.DouyinInfo(
                $"[reply-send] reply_id={job.ReplyId} target_user={job.UserId} " +
                $"content={Truncate(job.Content)} result=skip_already_sent");
            return;
        }

        // 侧车已发出弹幕但回显格式不完全一致——确认已发送后再 Track
        if (IsLikelyAlreadySent(reason))
        {
            _idempotency.MarkSucceeded(job.ReplyId);
            RecordSent();
            _outboundTracker?.Track(job.ReplyId, job.Content, detail.PlatformMessageId, job.WebRid);
            _log.DouyinInfo(
                $"REPLY_QUEUE replyId={job.ReplyId} type={(job.IsSongRequestBatch ? "song_request_batch" : "mention")} " +
                $"sendAt={DateTime.UtcNow:O} retry={job.RetryCount} success=true result=ok_likely_sent error={reason}");
            return;
        }

        job.RetryCount++;
            if (job.RetryCount <= _settings.MaxRetries
                && !DouyinService.IsNotLoggedIn(reason)
                && !DouyinService.IsRoomMismatch(reason)
                && !DouyinService.IsPermissionDenied(reason))
            {
                _log.DouyinWarn(
                    $"REPLY_QUEUE replyId={job.ReplyId} type={(job.IsSongRequestBatch ? "song_request_batch" : "mention")} " +
                    $"retry={job.RetryCount} success=false error={reason}");

                try
            {
                var delayMs = DouyinService.IsWriteCredentialPending(reason, detail.HttpStatus)
                    ? Math.Max(_settings.RetryDelayMs, 3000)
                    : _settings.RetryDelayMs;
                await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_channel.Writer.TryWrite(job))
            {
                Interlocked.Increment(ref _pendingDepth);
            }

            return;
        }

        if (DouyinService.IsNotLoggedIn(reason))
        {
            _log.DouyinWarn(
                $"REPLY_QUEUE replyId={job.ReplyId} result=fail error=not_logged_in " +
                $"ui=抖音未登录（停止重试）");
            var nowLogin = DateTime.UtcNow;
            if (nowLogin - _lastFailureNoticeUtc > TimeSpan.FromSeconds(30))
            {
                _lastFailureNoticeUtc = nowLogin;
                _onSendFailed?.Invoke("抖音未登录");
            }

            return;
        }

        if (DouyinService.IsPermissionDenied(reason))
        {
            _log.DouyinWarn($"DOUYIN_PERMISSION_DENIED replyId={job.ReplyId} error={reason}");
            var nowPerm = DateTime.UtcNow;
            if (nowPerm - _lastFailureNoticeUtc > TimeSpan.FromSeconds(30))
            {
                _lastFailureNoticeUtc = nowPerm;
                _onSendFailed?.Invoke("抖音账号无发送权限（请确认 Chrome 登录的是主播账号）");
            }

            return;
        }

        _log.Error(
            "douyin",
            $"REPLY_QUEUE replyId={job.ReplyId} type={(job.IsSongRequestBatch ? "song_request_batch" : "mention")} " +
            $"retry={job.RetryCount} success=false result=fail error={reason}");

        var now = DateTime.UtcNow;
        if (now - _lastFailureNoticeUtc > TimeSpan.FromSeconds(30))
        {
            _lastFailureNoticeUtc = now;
            var tip = KuaishouService.IsKuaishouRoom(job.WebRid)
                ? $"快手弹幕@回复失败：{reason}。请检查 ks 侧车、Cookie 与房间连接"
                : DouyinService.IsBizAuthFailure(reason, detail.HttpStatus)
                  || DouyinService.IsWriteCredentialPending(reason, detail.HttpStatus)
                    ? $"弹幕@回复失败：{reason}。请在抖音 CDP 里完成扫码登录后再试"
                    : DouyinService.IsRoomMismatch(reason)
                        ? $"弹幕@回复失败：监听房间与发送房间不一致"
                        : $"弹幕@回复失败：{reason}";
            _onSendFailed?.Invoke(tip);
        }
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
        try { _batchCts?.Cancel(); } catch { /* ignore */ }
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
        try { _batchCts?.Dispose(); } catch { /* ignore */ }
        _batchCts = null;
    }
}
