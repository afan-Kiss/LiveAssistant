using System.Text.Json.Serialization;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 点歌开关与用户限制（每日次数等），不修改点歌核心流程。
/// </summary>
public sealed class SongRequestControlService
{
    private readonly ConfigManager _config;
    private readonly UserRepository _users;

    public SongRequestControlService(ConfigManager config, UserRepository users)
    {
        _config = config;
        _users = users;
    }

    public bool IsRequestEnabled =>
        _config.Settings.SongRequestPolicy.RequestEnabled
        && !_config.Settings.Emergency.PauseSongRequest;

    public string PausedReplyMessage =>
        string.IsNullOrWhiteSpace(_config.Settings.SongRequestPolicy.PausedReplyMessage)
            ? "当前暂停点歌，请稍后再试"
            : _config.Settings.SongRequestPolicy.PausedReplyMessage;

    public void SetRequestEnabled(bool enabled)
    {
        _config.Settings.SongRequestPolicy.RequestEnabled = enabled;
        _config.Settings.Emergency.PauseSongRequest = !enabled;
        _config.Save();
    }

    public SongRequestControlSettings GetSettings()
    {
        var policy = _config.Settings.SongRequestPolicy;
        var queue = _config.Settings.Queue;
        return new SongRequestControlSettings
        {
            RequestEnabled = policy.RequestEnabled,
            RequireConfirm = policy.RequireConfirm,
            PausedReplyMessage = policy.PausedReplyMessage,
            MaxDailyRequestsPerUser = policy.MaxDailyRequestsPerUser,
            CooldownSeconds = queue.SongRequestCooldownSeconds,
            MaxQueueSize = queue.MaxSize,
            Mode = policy.Mode,
            PointsCost = policy.PointsCost,
            SkipPointsCost = policy.SkipPointsCost,
            GiftUnlockMinPoints = policy.GiftUnlockMinPoints,
            MinLevel = policy.MinLevel,
            RequiredGiftName = policy.RequiredGiftName
        };
    }

    public void SaveSettings(SongRequestControlSettings settings)
    {
        var policy = _config.Settings.SongRequestPolicy;
        policy.RequestEnabled = settings.RequestEnabled;
        policy.RequireConfirm = true;
        policy.PausedReplyMessage = settings.PausedReplyMessage ?? "当前暂停点歌，请稍后再试";
        policy.MaxDailyRequestsPerUser = Math.Max(0, settings.MaxDailyRequestsPerUser);
        policy.Mode = settings.Mode;
        policy.PointsCost = settings.PointsCost;
        policy.SkipPointsCost = settings.SkipPointsCost;
        policy.GiftUnlockMinPoints = settings.GiftUnlockMinPoints;
        policy.MinLevel = settings.MinLevel;
        policy.RequiredGiftName = settings.RequiredGiftName ?? "";

        _config.Settings.Queue.SongRequestCooldownSeconds = Math.Max(0, settings.CooldownSeconds);
        _config.Settings.Queue.MaxSize = Math.Max(1, settings.MaxQueueSize);
        _config.Settings.Emergency.PauseSongRequest = !settings.RequestEnabled;
        _config.Save();
    }

    public SongRequestGateResult? EvaluateGate(DanmakuItem item)
    {
        if (!IsRequestEnabled)
        {
            return SongRequestGateResult.Deny(PausedReplyMessage);
        }

        var maxDaily = _config.Settings.SongRequestPolicy.MaxDailyRequestsPerUser;
        if (maxDaily > 0 && _users.CountRequestsToday(item.UserId) >= maxDaily)
        {
            return SongRequestGateResult.Deny("今日点歌次数已达上限");
        }

        return null;
    }
}

public sealed class SongRequestControlSettings
{
    public bool RequestEnabled { get; set; } = true;
    /// <summary>搜到歌后固定 @ 询问，用户回复「确定」才入队（始终开启）。</summary>
    public bool RequireConfirm { get; set; } = true;
    public string PausedReplyMessage { get; set; } = "当前暂停点歌，请稍后再试";
    public int MaxDailyRequestsPerUser { get; set; }
    public int CooldownSeconds { get; set; } = 30;
    public int MaxQueueSize { get; set; } = 50;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SongRequestPolicyMode Mode { get; set; } = SongRequestPolicyMode.Free;
    public int PointsCost { get; set; } = 10;
    public int SkipPointsCost { get; set; } = 20;
    public int GiftUnlockMinPoints { get; set; } = 100;
    public int MinLevel { get; set; }
    public string RequiredGiftName { get; set; } = "";
}

public sealed class SongRequestGateResult
{
    public string Reason { get; private set; } = "";

    public static SongRequestGateResult Deny(string reason) => new() { Reason = reason };
}
