namespace LiveAssistant.Services;

/// <summary>
/// 直播运行健康指标（今日统计、近期错误），供主界面与后台 API 使用。
/// </summary>
public sealed class LiveHealthService
{
    private readonly object _lock = new();
    private DateTime _statsDate = DateTime.Today;
    private int _danmakuToday;
    private int _giftsToday;
    private int _songRequestsToday;
    private int _songsPlayedToday;
    private int _recentErrorCount;
    private DateTime _errorWindowStart = DateTime.Now;
    private string _lastPlayedKey = "";

    public void RecordDanmaku()
    {
        ResetIfNewDay();
        lock (_lock)
        {
            _danmakuToday++;
        }
    }

    public void RecordGift()
    {
        ResetIfNewDay();
        lock (_lock)
        {
            _giftsToday++;
        }
    }

    public void RecordSongRequest()
    {
        ResetIfNewDay();
        lock (_lock)
        {
            _songRequestsToday++;
        }
    }

    public void RecordSongPlayed(string? trackKey)
    {
        if (string.IsNullOrWhiteSpace(trackKey))
        {
            return;
        }

        ResetIfNewDay();
        lock (_lock)
        {
            if (_lastPlayedKey == trackKey)
            {
                return;
            }

            _lastPlayedKey = trackKey;
            _songsPlayedToday++;
        }
    }

    public void RecordError()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            if ((now - _errorWindowStart).TotalHours >= 1)
            {
                _errorWindowStart = now;
                _recentErrorCount = 0;
            }

            _recentErrorCount++;
        }
    }

    public LiveHealthSnapshot GetSnapshot()
    {
        ResetIfNewDay();
        lock (_lock)
        {
            return new LiveHealthSnapshot
            {
                TodayDanmakuCount = _danmakuToday,
                TodayGiftCount = _giftsToday,
                TodaySongRequestCount = _songRequestsToday,
                TodaySongsPlayed = _songsPlayedToday,
                RecentErrorCount = _recentErrorCount
            };
        }
    }

    private void ResetIfNewDay()
    {
        var today = DateTime.Today;
        lock (_lock)
        {
            if (_statsDate == today)
            {
                return;
            }

            _statsDate = today;
            _danmakuToday = 0;
            _giftsToday = 0;
            _songRequestsToday = 0;
            _songsPlayedToday = 0;
            _lastPlayedKey = "";
        }
    }
}

public sealed class LiveHealthSnapshot
{
    public int TodayDanmakuCount { get; set; }
    public int TodayGiftCount { get; set; }
    public int TodaySongRequestCount { get; set; }
    public int TodaySongsPlayed { get; set; }
    public int RecentErrorCount { get; set; }
}
