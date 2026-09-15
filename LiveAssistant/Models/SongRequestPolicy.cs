using System.Text.Json.Serialization;

namespace LiveAssistant.Models;

public enum SongRequestPolicyMode
{
    Free,
    Points,
    GiftUnlock
}

public sealed class SongRequestPolicySettings
{
    /// <summary>点歌总开关；关闭时回复 PausedReplyMessage。</summary>
    public bool RequestEnabled { get; set; } = true;

    /// <summary>是否需要用户回复「确定」才入队；关闭则搜到歌后直接播放。</summary>
    /// <summary>保留配置字段；点歌流程始终需要回复「确定」后才入队。</summary>
    public bool RequireConfirm { get; set; } = true;

    public string PausedReplyMessage { get; set; } = "当前暂停点歌，请稍后再试";

    /// <summary>每用户每日最大点歌次数；0 表示不限制。</summary>
    public int MaxDailyRequestsPerUser { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SongRequestPolicyMode Mode { get; set; } = SongRequestPolicyMode.Free;
    public int PointsCost { get; set; } = 10;
    /// <summary>积分点歌模式下，弹幕「切歌」消耗的积分；免费/礼物解锁模式不扣。</summary>
    public int SkipPointsCost { get; set; } = 20;
    public int GiftUnlockMinPoints { get; set; } = 100;
    public int MinLevel { get; set; }
    public string RequiredGiftName { get; set; } = "";
}

public sealed class LevelPermission
{
    public int Level { get; set; }
    public bool CanRequest { get; set; } = true;
    public int CooldownSeconds { get; set; } = 30;
    public int MinPoints { get; set; }
    public int QueuePriority { get; set; }
    public int PointsCostOverride { get; set; } = -1;
}
