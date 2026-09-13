namespace LiveAssistant.Models;

public sealed class RuntimeStatus
{
    public bool DouyinOnline { get; set; }
    public bool KugouOnline { get; set; }
    public string DouyinStatus { get; set; } = "未检测";
    public string KugouStatus { get; set; } = "未检测";
    public string DanmakuConnection { get; set; } = "未连接";
    public string CurrentSong { get; set; } = "-";
    public int QueueCount { get; set; }
    public TimeSpan Uptime { get; set; }
}
