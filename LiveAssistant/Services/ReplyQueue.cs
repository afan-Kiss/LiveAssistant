using System.Threading.Channels;
using LiveAssistant.Config;

namespace LiveAssistant.Services;

public sealed class ReplyJob
{
    public required string WebRid { get; init; }
    public required string UserId { get; init; }
    public required string Content { get; init; }
    public int RetryCount { get; set; }
}

/// <summary>
/// 弹幕回复统一发送队列：限速、防刷屏、失败重试。
/// </summary>
public sealed class ReplyQueue : IDisposable
{
    private readonly Channel<ReplyJob> _channel =
        Channel.CreateUnbounded<ReplyJob>(new UnboundedChannelOptions { SingleReader = true });

    private readonly DouyinService _douyin;
    private readonly LogService _log;
    private readonly ReplySettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly Queue<DateTime> _sentTimestamps = new();
    private readonly object _rateLock = new();

    public ReplyQueue(DouyinService douyin, LogService log, ReplySettings settings)
    {
        _douyin = douyin;
        _log = log;
        _settings = settings;
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public void EnqueueMention(string webRid, string userId, string content)
    {
        if (string.IsNullOrWhiteSpace(webRid) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        if (!_channel.Writer.TryWrite(new ReplyJob
        {
            WebRid = webRid,
            UserId = userId,
            Content = content
        }))
        {
            _log.DouyinWarn("回复入队失败");
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
                    await WaitForRateLimitAsync(ct);
                    var ok = await _douyin.SendMentionAsync(job.WebRid, job.UserId, job.Content, ct);
                    if (ok)
                    {
                        RecordSent();
                        _log.DouyinInfo($"回复已发送 user={job.UserId}");
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
        job.RetryCount++;
        if (job.RetryCount <= _settings.MaxRetries)
        {
            _log.DouyinWarn($"回复发送失败，重试 {job.RetryCount}/{_settings.MaxRetries}: {reason}");
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

        _log.Error("douyin", $"回复发送失败且已达重试上限 user={job.UserId} reason={reason}");
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

    public void Dispose()
    {
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
    }
}
