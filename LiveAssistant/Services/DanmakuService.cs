using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class DanmakuService : IDisposable
{
    private readonly DouyinService _douyin;
    private readonly LogService _log;
    private readonly SystemMessageService _system;
    private CancellationTokenSource? _cts;
    private int _after;
    private string _webRid = "";

    public event Action<DanmakuItem>? DanmakuReceived;

    public bool IsRunning { get; private set; }
    public string ConnectionStatus { get; private set; } = "未连接";
    public string AccountNickname { get; private set; } = "-";
    public string RoomTitle { get; private set; } = "-";

    public DanmakuService(DouyinService douyin, LogService log, SystemMessageService system)
    {
        _douyin = douyin;
        _log = log;
        _system = system;
    }

    public async Task StartAsync(string webRid, CancellationToken ct = default)
    {
        _webRid = webRid.Trim();
        if (string.IsNullOrEmpty(_webRid))
        {
            throw new InvalidOperationException("请配置直播间 web_rid");
        }

        Stop();

        var health = await _douyin.GetHealthAsync(ct);
        AccountNickname = health?.Nickname ?? "-";

        var room = await _douyin.ResolveRoomAsync(_webRid, ct);
        RoomTitle = room?.Title ?? _webRid;

        await _douyin.StartCollectAsync(_webRid, ct);
        _after = 0;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        ConnectionStatus = "已连接";
        _system.Add("直播间监控已启动");

        _ = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        IsRunning = false;
        ConnectionStatus = "已停止";
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var failCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var feed = await _douyin.PollDanmakuAsync(_webRid, _after, 50, ct);
                if (feed != null)
                {
                    failCount = 0;
                    ConnectionStatus = feed.Running ? "已连接" : "监控中";
                    _after = feed.MessageCount;

                    if (feed.Items != null)
                    {
                        foreach (var msg in feed.Items)
                        {
                            if (string.IsNullOrWhiteSpace(msg.Content))
                            {
                                continue;
                            }

                            var item = new DanmakuItem
                            {
                                MsgId = msg.MsgId ?? Guid.NewGuid().ToString("N"),
                                Content = msg.Content,
                                Nickname = msg.User?.Nickname ?? "未知",
                                UserId = msg.User?.UserId ?? "",
                                MsgType = msg.MsgType ?? "chat",
                                Timestamp = DateTime.Now
                            };
                            DanmakuReceived?.Invoke(item);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failCount++;
                _log.Warn($"弹幕轮询失败({failCount}): {ex.Message}");
                ConnectionStatus = "连接异常";
                if (failCount >= 3)
                {
                    _system.Add("连接断开，正在重连...");
                    try
                    {
                        await _douyin.ReconnectAsync(_webRid, ct);
                        await _douyin.StartCollectAsync(_webRid, ct);
                        _system.Add("重连成功");
                        failCount = 0;
                    }
                    catch (Exception rex)
                    {
                        _log.Warn($"重连失败: {rex.Message}");
                    }
                }
            }

            try
            {
                await Task.Delay(1500, ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
