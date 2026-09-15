namespace LiveAssistant.Models;

public sealed class RuntimeStatus
{
    public bool DouyinOnline { get; set; }
    public bool KugouOnline { get; set; }
    public string DouyinStatus { get; set; } = "未检测";
    public string KugouStatus { get; set; } = "未检测";
    public string KugouLoginStatus { get; set; } = "未检测";
    public string KugouVipLabel { get; set; } = "";
    public bool KugouFullPlaybackAvailable { get; set; }
    public string KugouFullPlaybackReason { get; set; } = "";
    public string KugouLoginPageUrl { get; set; } = "";
    public string DanmakuConnection { get; set; } = "未连接";
    public string RoomOwnerNickname { get; set; } = "-";
    public string DouyinLoginStatus { get; set; } = "未检测";
    public string DouyinLoginNickname { get; set; } = "-";

    /// <summary>兼容后台 API。</summary>
    public string AdminAccountStatus => DouyinLoginStatus;

    /// <summary>兼容后台 API。</summary>
    public string AdminNickname => DouyinLoginNickname;
    public string CurrentSong { get; set; } = "-";
    public string PlaybackMode { get; set; } = "-";
    public int QueueCount { get; set; }
    public TimeSpan Uptime { get; set; }
    public DateTime StartedAt { get; set; }
    public string CurrentTask { get; set; } = "空闲";
    public string LastError { get; set; } = "";

    public string PlaybackState { get; set; } = "-";
    public string PlaybackSource { get; set; } = "-";
    public int ProgressSec { get; set; }
    public int DurationSec { get; set; }
    public int RemainingSec { get; set; }
    public bool SongRequestEnabled { get; set; } = true;

    public int TodaySongsPlayed { get; set; }
    public int TodayDanmakuCount { get; set; }
    public int TodayGiftCount { get; set; }
    public int TodaySongRequestCount { get; set; }
    public int RecentErrorCount { get; set; }
    public int WaitingQueueCount { get; set; }
}
