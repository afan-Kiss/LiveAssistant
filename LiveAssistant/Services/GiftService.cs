using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

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
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void HandleGiftEvent(GiftEvent gift)
    {
        gift.Id = _gifts.Insert(gift);
        var rulePoints = _giftRules.GetPointsForGift(gift.GiftName);
        var points = rulePoints ?? (gift.Value * _config.Settings.Gift.PointsPerValue);
        points *= gift.Count;
        if (points > 0)
        {
            _users.AddPoints(gift.UserId, gift.Nickname, points);
            _levels.RefreshUserLevel(gift.UserId);
        }

        _system.Add($"礼物: {gift.Nickname} 送出 {gift.GiftName}×{gift.Count}");
        _log.Info($"礼物 user={gift.Nickname} gift={gift.GiftName} count={gift.Count} value={gift.Value}");
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
