namespace LiveAssistant.Models;

public sealed class RuntimeStatus
{
    public bool DouyinOnline { get; set; }
    public bool KugouOnline { get; set; }
    public string DouyinStatus { get; set; } = "未检测";
    public string KugouStatus { get; set; } = "未检测";
    public string DanmakuConnection { get; set; } = "未连接";
    public string AdminAccountStatus { get; set; } = "未检测";
    public string AdminNickname { get; set; } = "-";
    public string CurrentSong { get; set; } = "-";
    public string PlaybackMode { get; set; } = "-";
    public int QueueCount { get; set; }
    public TimeSpan Uptime { get; set; }
    public DateTime StartedAt { get; set; }
    public string CurrentTask { get; set; } = "空闲";
    public string LastError { get; set; } = "";
}
