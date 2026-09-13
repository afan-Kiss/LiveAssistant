using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 礼物由管理员账号登录的抖音 Sidecar 采集，不限制主播身份。
/// </summary>
public sealed class GiftService : IDisposable
{
    private readonly ConfigManager _config;
    private readonly DouyinService _douyin;
    private readonly GiftRepository _gifts;
    private readonly UserRepository _users;
    private readonly UserLevelService _levels;
    private readonly GiftRuleRepository _giftRules;
    private readonly LogService _log;
    private readonly SystemMessageService _system;
    private CancellationTokenSource? _cts;
    private int _after;

    public event Action<GiftEvent>? GiftReceived;

    public GiftService(
        ConfigManager config,
        DouyinService douyin,
        GiftRepository gifts,
        UserRepository users,
        UserLevelService levels,
        GiftRuleRepository giftRules,
        LogService log,
        SystemMessageService system)
    {
        _config = config;
        _douyin = douyin;
        _gifts = gifts;
        _users = users;
        _levels = levels;
        _giftRules = giftRules;
        _log = log;
        _system = system;
    }

    public void Start(string webRid)
    {
        Stop();
        _after = 0;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PollLoopAsync(webRid, _cts.Token));
        _log.GiftInfo("礼物轮询已启动（管理员账号 Sidecar 采集）");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void HandleGiftEvent(GiftEvent gift)
    {
        var before = _users.GetUser(gift.UserId)?.Points ?? 0;
        var rule = _giftRules.FindByGift(gift.GiftName, gift.GiftId);
        var points = (rule?.Points ?? (gift.Value * _config.Settings.Gift.PointsPerValue)) * gift.Count;

        gift.Id = _gifts.Insert(gift, points, before + points);
        if (points > 0)
        {
            _users.AddPoints(gift.UserId, gift.Nickname, points);
            _levels.RefreshUserLevel(gift.UserId);
        }

        var after = _users.GetUser(gift.UserId)?.Points ?? before + points;
        _log.LogGift(gift.UserId, gift.Nickname, gift.GiftName, gift.Count, points, after);
        _system.Add($"礼物: {gift.Nickname} 送出 {gift.GiftName}×{gift.Count} (+{points}积分)");
        GiftReceived?.Invoke(gift);
    }

    private async Task PollLoopAsync(string webRid, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var feed = await _douyin.PollGiftAsync(webRid, _after, 50, ct);
                if (feed?.Items != null)
                {
                    _after = feed.GiftCount;
                    foreach (var raw in feed.Items)
                    {
                        var gift = MapGift(raw);
                        if (gift != null)
                        {
                            HandleGiftEvent(gift);
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
                _log.GiftWarn($"礼物轮询异常: {ex.Message}");
                _log.Error("gift", "礼物轮询异常", ex);
            }

            try
            {
                await Task.Delay(_config.Settings.Gift.PollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static GiftEvent? MapGift(DouyinGiftMessage raw)
    {
        var userId = raw.User?.UserId ?? raw.UserId ?? "";
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var time = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(raw.Time) && DateTime.TryParse(raw.Time, out var parsed))
        {
            time = parsed;
        }

        return new GiftEvent
        {
            UserId = userId,
            Nickname = raw.User?.Nickname ?? raw.Nickname ?? "",
            GiftId = raw.GiftId ?? "",
            GiftName = raw.GiftName ?? "礼物",
            Count = raw.Count <= 0 ? 1 : raw.Count,
            Value = raw.Value,
            Time = time,
            CreatedAt = time
        };
    }

    public void Dispose() => Stop();
}
