using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

public sealed class DanmakuService : IDisposable
{
    private readonly DouyinService _douyin;
    private readonly LogService _log;
    private readonly SystemMessageService _system;
    private readonly DanmakuDeduplicator _deduper;
    private CancellationTokenSource? _cts;
    private int _after;
    private int _afterAt;
    private string _webRid = "";

    public event Action<DanmakuItem>? DanmakuReceived;

    public bool IsRunning { get; private set; }
    public string ConnectionStatus { get; private set; } = "未连接";
    /// <summary>直播间主播昵称（来自 room/resolve 的 owner）。</summary>
    public string RoomOwnerNickname { get; private set; } = "-";

    /// <summary>抖音侧车 Cookie 登录昵称（用于 @ 回复/房管操作）。</summary>
    public string DouyinLoginNickname { get; private set; } = "-";

    public string RoomTitle { get; private set; } = "-";

    public DanmakuService(
        DouyinService douyin,
        LogService log,
        SystemMessageService system,
        DanmakuDeduplicator? deduper = null)
    {
        _douyin = douyin;
        _log = log;
        _system = system;
        _deduper = deduper ?? new DanmakuDeduplicator();
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
        DouyinLoginNickname = health?.Nickname ?? "-";

        var room = await _douyin.ResolveRoomAsync(_webRid, ct);
        RoomOwnerNickname = room?.Owner?.Nickname ?? "-";
        RoomTitle = room?.Title ?? _webRid;

        await _douyin.StartCollectAsync(_webRid, ct);
        _after = 0;
        _afterAt = 0;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        ConnectionStatus = "已连接";
        _system.Add("直播间监控已启动");
        _log.DouyinInfo($"弹幕监控启动 web_rid={_webRid}");

        _ = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        IsRunning = false;
        ConnectionStatus = "已停止";
        _log.DouyinInfo("弹幕监控已停止");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var failCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var feed = await _douyin.PollDanmakuAsync(_webRid, _after, 50, ct);
                var atFeed = await _douyin.PollAtDanmakuAsync(_webRid, _afterAt, 50, ct);
                if (feed != null || atFeed != null)
                {
                    failCount = 0;
                    ConnectionStatus = (feed?.Running ?? atFeed?.Running ?? false) ? "已连接" : "监控中";
                    if (feed != null)
                    {
                        _after = feed.MessageCount;
                        DispatchItems(feed.Items);
                    }

                    if (atFeed != null)
                    {
                        _afterAt = atFeed.MentionCount > 0 ? atFeed.MentionCount : atFeed.MessageCount;
                        DispatchItems(atFeed.Items);
                    }
                }
                else
                {
                    failCount++;
                    ConnectionStatus = "等待抖音API";
                    if (failCount >= 3)
                    {
                        _system.Add("连接断开，正在重连...");
                        try
                        {
                            await _douyin.ReconnectAsync(_webRid, ct);
                            await _douyin.StartCollectAsync(_webRid, ct);
                            _system.Add("重连成功");
                            _log.DouyinInfo("弹幕重连成功");
                            failCount = 0;
                        }
                        catch (Exception rex)
                        {
                            _log.DouyinWarn($"重连失败: {rex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failCount++;
                _log.DouyinWarn($"弹幕轮询失败({failCount}): {ex.Message}");
                _log.Error("douyin", "poll_loop", ex);
                ConnectionStatus = "连接异常";
                if (failCount >= 3)
                {
                    _system.Add("连接断开，正在重连...");
                    try
                    {
                        await _douyin.ReconnectAsync(_webRid, ct);
                        await _douyin.StartCollectAsync(_webRid, ct);
                        _system.Add("重连成功");
                        _log.DouyinInfo("弹幕重连成功");
                        failCount = 0;
                    }
                    catch (Exception rex)
                    {
                        _log.Error("douyin", "reconnect", rex);
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
            catch (Exception ex)
            {
                _log.Error("douyin", "poll_delay", ex);
            }
        }
    }

    private void DispatchItems(List<DouyinDanmakuMessage>? items)
    {
        if (items == null)
        {
            return;
        }

        foreach (var msg in items)
        {
            var msgType = msg.MsgType ?? "chat";
            if (string.IsNullOrWhiteSpace(msg.Content) && msgType == "chat")
            {
                continue;
            }

            try
            {
                var msgId = ResolveMsgId(msg);
                var userId = msg.User?.UserId ?? "";
                var nickname = msg.User?.Nickname ?? "未知";
                var content = msg.Content ?? "";

                _log.DouyinInfo(
                    $"[danmaku-recv] msg_id={msgId} user_id={userId} nickname={nickname} content={Truncate(content)}");

                if (!_deduper.TryAdmit(msgId))
                {
                    _log.DouyinInfo($"[danmaku-dedupe] drop msg_id={msgId}");
                    continue;
                }

                if (ShouldIgnoreBotMessage(nickname, content))
                {
                    _log.DouyinInfo($"[danmaku-bot] drop self/bot content={Truncate(content)}");
                    continue;
                }

                if (!_deduper.TryAdmitUserContent(userId, content))
                {
                    _log.DouyinInfo($"[danmaku-dedupe] drop duplicate content user={userId}");
                    continue;
                }

                var item = new DanmakuItem
                {
                    MsgId = msgId,
                    Content = content,
                    Nickname = nickname,
                    UserId = userId,
                    MsgType = msgType,
                    Timestamp = DateTime.Now
                };
                DanmakuReceived?.Invoke(item);
            }
            catch (Exception ex)
            {
                _log.Error("douyin", "处理弹幕项异常", ex);
            }
        }
    }

    private bool ShouldIgnoreBotMessage(string nickname, string content)
    {
        if (SongNameParser.IsBotReply(content))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(DouyinLoginNickname)
            && !DouyinLoginNickname.Equals("-", StringComparison.Ordinal)
            && nickname.Equals(DouyinLoginNickname, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string ResolveMsgId(DouyinDanmakuMessage msg)
    {
        if (!string.IsNullOrWhiteSpace(msg.MsgId))
        {
            return msg.MsgId.Trim();
        }

        // Sidecar 未给 msg_id 时用稳定回退键，避免每次 Guid 导致无法去重
        var uid = msg.User?.UserId?.Trim() ?? "";
        var content = msg.Content?.Trim() ?? "";
        var ts = msg.Timestamp?.Trim() ?? "";
        return $"fb:{uid}|{content}|{ts}";
    }

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
        Stop();
    }
}
