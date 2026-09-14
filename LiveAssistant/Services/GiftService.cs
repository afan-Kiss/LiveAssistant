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
    private string _webRid = "";

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

        var pointsAfter = before + points;

        if (!_gifts.TryInsert(gift, points, pointsAfter, out var id))
        {
            _log.LogGiftDuplicate(gift.Nickname, gift.GiftName, gift.EventId, "插入冲突");
            return false;
        }

        gift.Id = id;
        if (points > 0)
        {
            _users.AddPoints(gift.UserId, gift.Nickname, points);
            _levels.RefreshUserLevel(gift.UserId);
        }

        ApplySongPermission(gift, rule);

        var after = _users.GetUser(gift.UserId)?.Points ?? pointsAfter;
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
        if (_replyQueue == null || _reply == null || string.IsNullOrWhiteSpace(_webRid))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(gift.UserId))
        {
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

        _replyQueue.EnqueueMention(_webRid, gift.UserId, msg);
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

    private void ApplySongPermission(GiftEvent gift, GiftRule? rule)
    {
        if (rule == null)
        {
            return;
        }

        var perm = rule.EffectiveSongPermissionCount();
        if (perm == -1)
        {
            _users.SetSongPermissionUnlimited(gift.UserId, true);
            return;
        }

        if (perm > 0)
        {
            _users.AddSongPermissionCredits(gift.UserId, perm * gift.Count);
        }
    }

    public void Dispose() => Stop();
}
