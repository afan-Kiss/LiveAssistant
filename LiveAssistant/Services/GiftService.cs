using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 礼物业务：积分 / 点歌资格。采集由 <see cref="GiftCollectorService"/> 负责。
/// </summary>
public sealed class GiftService : IDisposable
{
    private readonly ConfigManager _config;
    private readonly GiftRepository _gifts;
    private readonly UserRepository _users;
    private readonly UserLevelService _levels;
    private readonly GiftRuleRepository _giftRules;
    private readonly LogService _log;
    private readonly SystemMessageService _system;
    private readonly ReplyService? _reply;
    private readonly ReplyQueue? _replyQueue;
    private readonly object _thanksLock = new();
    private readonly Dictionary<string, GiftThanksWindow> _thanksWindows = new(StringComparer.Ordinal);
    private string _webRid = "";

    private static readonly TimeSpan GiftThanksWindowDuration = TimeSpan.FromSeconds(10);
    private const int GiftThanksMaxPerWindow = 3;

    public event Action<GiftEvent>? GiftReceived;

    public GiftService(
        ConfigManager config,
        DouyinService douyin,
        GiftRepository gifts,
        UserRepository users,
        UserLevelService levels,
        GiftRuleRepository giftRules,
        LogService log,
        SystemMessageService system,
        ReplyService? reply = null,
        ReplyQueue? replyQueue = null)
    {
        _ = douyin;
        _config = config;
        _gifts = gifts;
        _users = users;
        _levels = levels;
        _giftRules = giftRules;
        _log = log;
        _system = system;
        _reply = reply;
        _replyQueue = replyQueue;
    }

    public void BindRoom(string webRid)
    {
        _webRid = webRid?.Trim() ?? "";
    }

    public void Start(string webRid)
    {
        BindRoom(webRid);
        _log.GiftInfo("GiftService 已绑定房间（礼物采集改由 GiftCollector）");
    }

    public void Stop()
    {
    }

    public bool ExistsByEventId(string eventId)
        => _gifts.ExistsByEventId(eventId);

    public bool HandleGiftEvent(GiftEvent gift)
    {
        if (string.IsNullOrWhiteSpace(gift.UserId))
        {
            _log.GiftWarn("[guard] 拒绝：缺少 UserId");
            return false;
        }

        if (string.IsNullOrWhiteSpace(gift.EventId))
        {
            gift.EventId = GiftEvent.BuildEventId(
                _webRid, gift.Sequence, gift.UserId, gift.GiftId, gift.GiftName, gift.Count,
                gift.Time.ToString("O"));
        }

        // 双通道：抖音采集常未填 RoomKey，回退当前绑定房间，供致谢/@ 与 AI 口播路由
        if (string.IsNullOrWhiteSpace(gift.RoomKey) && !string.IsNullOrWhiteSpace(_webRid))
        {
            gift.RoomKey = _webRid;
        }

        if (!TryValidateAmounts(gift, out var guardReason))
        {
            _log.GiftWarn($"[guard] 拒绝异常金额 eventId={gift.EventId} reason={guardReason} " +
                          $"count={gift.Count} value={gift.Value} diamond={gift.DiamondCount}");
            return false;
        }

        if (_gifts.ExistsByEventId(gift.EventId))
        {
            _log.LogGiftDuplicate(gift.Nickname, gift.GiftName, gift.EventId, "事件已存在(DB)");
            return false;
        }

        var before = _users.GetUser(gift.UserId)?.Points ?? 0;
        var rule = _giftRules.FindByGift(gift.GiftName, gift.GiftId);
        // Value = 本次礼物总钻石价值，禁止再乘 Count。
        var points = rule?.Points is int rulePoints
            ? rulePoints * Math.Max(1, gift.Count)
            : gift.Value * _config.Settings.Gift.PointsPerValue;

        if (points < 0)
        {
            _log.GiftWarn($"[guard] 拒绝负积分 eventId={gift.EventId} points={points}");
            return false;
        }

        var maxPoints = Math.Max(0, _config.Settings.Gift.MaxPointsDelta);
        if (maxPoints > 0 && points > maxPoints)
        {
            _log.GiftWarn($"[guard] 拒绝超限积分 eventId={gift.EventId} points={points} max={maxPoints}");
            return false;
        }

        // pointsAfter/level 仅作占位；真实余额在 GiftRepository 事务内计算
        var pointsAfterHint = before + points;
        var newLevelHint = points > 0 ? _levels.CalculateLevel(pointsAfterHint) : (_users.GetUser(gift.UserId)?.Level ?? 0);
        var songPermissionUnlimited = false;
        var songPermissionCredits = 0;
        if (rule != null)
        {
            var perm = rule.EffectiveSongPermissionCount();
            if (perm == -1)
            {
                songPermissionUnlimited = true;
            }
            else if (perm > 0)
            {
                songPermissionCredits = perm * gift.Count;
            }
        }

        if (!_gifts.TryRecordGift(
                gift,
                points,
                pointsAfterHint,
                newLevelHint,
                applyPointsAndLevel: points > 0,
                setSongPermissionUnlimited: songPermissionUnlimited,
                songPermissionCreditsDelta: songPermissionCredits,
                out var id,
                calculateLevel: pts => _levels.CalculateLevel(pts)))
        {
            _log.LogGiftDuplicate(gift.Nickname, gift.GiftName, gift.EventId, "插入冲突或事务回滚");
            return false;
        }

        gift.Id = id;

        var after = _users.GetUser(gift.UserId)?.Points ?? pointsAfterHint;
        _log.GiftInfo(
            $"[points] eventId={gift.EventId} user={gift.UserId}/{gift.Nickname} " +
            $"gift={gift.GiftName} count={gift.Count} value={gift.Value} " +
            $"pointsDelta={points} pointsAfter={after} rule={(rule?.GiftName ?? "-")}");
        _log.LogGift(gift.UserId, gift.Nickname, gift.GiftName, gift.Count, points, after);
        _system.Add($"礼物: {gift.Nickname} 送出 {gift.GiftName}×{gift.Count} (+{points}积分)");
        TrySendGiftThanks(gift);
        GiftReceived?.Invoke(gift);
        return true;
    }

    private void TrySendGiftThanks(GiftEvent gift)
    {
        var room = !string.IsNullOrWhiteSpace(gift.RoomKey) ? gift.RoomKey : _webRid;
        if (_replyQueue == null || _reply == null || string.IsNullOrWhiteSpace(room))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(gift.UserId))
        {
            return;
        }

        // 电影评分引导已覆盖「感谢礼物」文案时，不再发第二条纯感谢，避免同一礼物刷屏。
        var movieNotify = _config.Settings.MovieInteraction.Notification;
        if (_config.Settings.MovieInteraction.Enabled
            && movieNotify is { Enabled: true }
            && gift.Value > 0)
        {
            _log.GiftInfo(
                $"[thanks-skip] movie score guide owns gift thank user={gift.UserId} gift={gift.GiftName}");
            return;
        }

        if (ShouldThrottleGiftThanks(gift))
        {
            _log.GiftInfo(
                $"[thanks-throttle] user={gift.UserId} gift={gift.GiftName} count={gift.Count}");
            return;
        }

        var msg = _reply.Render("giftThanks", new Dictionary<string, string>
        {
            ["name"] = gift.Nickname,
            ["gift"] = gift.GiftName,
            ["count"] = gift.Count.ToString()
        });
        if (string.IsNullOrWhiteSpace(msg))
        {
            msg = $"感谢送出 {gift.GiftName}×{gift.Count}";
        }

        _replyQueue.EnqueueMention(room, gift.UserId, msg, nickname: gift.Nickname);
    }

    private bool ShouldThrottleGiftThanks(GiftEvent gift)
    {
        var key = $"{gift.RoomKey}|{gift.UserId}|{gift.GiftName}";
        var now = DateTime.UtcNow;
        lock (_thanksLock)
        {
            PurgeThanksWindowsLocked(now);
            if (_thanksWindows.Count > 2000)
            {
                // 长播防字典膨胀：清空窗口后重新计数
                _thanksWindows.Clear();
            }

            if (!_thanksWindows.TryGetValue(key, out var window)
                || now - window.StartedUtc > GiftThanksWindowDuration)
            {
                _thanksWindows[key] = new GiftThanksWindow { Count = 1, StartedUtc = now };
                return false;
            }

            window.Count++;
            return window.Count > GiftThanksMaxPerWindow;
        }
    }

    private void PurgeThanksWindowsLocked(DateTime now)
    {
        foreach (var key in _thanksWindows
                     .Where(kv => now - kv.Value.StartedUtc > GiftThanksWindowDuration)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _thanksWindows.Remove(key);
        }
    }

    private bool TryValidateAmounts(GiftEvent gift, out string reason)
    {
        reason = "";
        var maxCount = Math.Max(1, _config.Settings.Gift.MaxGiftCount);
        var maxValue = Math.Max(1, _config.Settings.Gift.MaxGiftValue);

        if (gift.Count <= 0)
        {
            reason = "count<=0";
            return false;
        }

        if (gift.Count > maxCount)
        {
            reason = $"count>{maxCount}";
            return false;
        }

        if (gift.Value < 0 || gift.DiamondCount < 0)
        {
            reason = "negative value/diamond";
            return false;
        }

        if (gift.Value > maxValue)
        {
            reason = $"value>{maxValue}";
            return false;
        }

        // Value 应为总钻石；若 DiamondCount 有值则校验一致性（允许 Value=0 的免费礼物）
        if (gift.DiamondCount > 0 && gift.Value > 0)
        {
            var expected = gift.DiamondCount * gift.Count;
            if (gift.Value != expected)
            {
                // 纠正为唯一口径，避免下游重复计算
                _log.GiftWarn(
                    $"[value] 校正 eventId={gift.EventId} from={gift.Value} to={expected} " +
                    $"(diamond={gift.DiamondCount} * count={gift.Count})");
                gift.Value = expected;
            }
        }

        return true;
    }

    public void Dispose() => Stop();

    private sealed class GiftThanksWindow
    {
        public int Count;
        public DateTime StartedUtc;
    }
}
