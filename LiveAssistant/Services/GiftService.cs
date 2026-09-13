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
        SystemMessageService system)
    {
        _ = douyin; // 保留构造签名，避免大规模 DI 改动
        _config = config;
        _gifts = gifts;
        _users = users;
        _levels = levels;
        _giftRules = giftRules;
        _log = log;
        _system = system;
    }

    /// <summary>绑定当前直播间短号（供 EventId 兜底）。</summary>
    public void BindRoom(string webRid)
    {
        _webRid = webRid?.Trim() ?? "";
    }

    /// <summary>兼容旧调用：仅绑定房间，不再轮询 Sidecar gift/feed。</summary>
    public void Start(string webRid)
    {
        BindRoom(webRid);
        _log.GiftInfo("GiftService 已绑定房间（礼物采集改由 GiftCollector）");
    }

    public void Stop()
    {
        // 采集停止由 GiftCollectorService 负责
    }

    public bool HandleGiftEvent(GiftEvent gift)
    {
        if (string.IsNullOrWhiteSpace(gift.EventId))
        {
            gift.EventId = GiftEvent.BuildEventId(
                _webRid, gift.Sequence, gift.UserId, gift.GiftId, gift.GiftName, gift.Count,
                gift.Time.ToString("O"));
        }

        if (_gifts.ExistsByEventId(gift.EventId))
        {
            _log.LogGiftDuplicate(gift.Nickname, gift.GiftName, gift.EventId, "事件已存在");
            return false;
        }

        var before = _users.GetUser(gift.UserId)?.Points ?? 0;
        var rule = _giftRules.FindByGift(gift.GiftName, gift.GiftId);
        var points = (rule?.Points ?? (gift.Value * _config.Settings.Gift.PointsPerValue)) * gift.Count;
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
        _log.LogGift(gift.UserId, gift.Nickname, gift.GiftName, gift.Count, points, after);
        _system.Add($"礼物: {gift.Nickname} 送出 {gift.GiftName}×{gift.Count} (+{points}积分)");
        GiftReceived?.Invoke(gift);
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
