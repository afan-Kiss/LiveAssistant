using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 可变上下文的观众消息过滤器，供 Danmaku / 词云 / AI 等模块共用。
/// </summary>
public sealed class ChatAudienceFilter
{
    private readonly OutboundReplyTracker _outboundTracker;
    private readonly object _lock = new();

    public ChatAudienceFilter(OutboundReplyTracker outboundTracker)
    {
        _outboundTracker = outboundTracker;
    }

    public string LoginUserId { get; private set; } = "";
    public string LoginNickname { get; private set; } = "-";
    public string RoomOwnerUserId { get; private set; } = "";
    public string RoomOwnerNickname { get; private set; } = "-";
    public string RoomKey { get; private set; } = "";

    public void UpdateDouyinIdentity(string? loginUserId, string? loginNickname, string? roomKey = null)
    {
        lock (_lock)
        {
            LoginUserId = loginUserId?.Trim() ?? "";
            LoginNickname = string.IsNullOrWhiteSpace(loginNickname) ? "-" : loginNickname.Trim();
            if (!string.IsNullOrWhiteSpace(roomKey))
            {
                RoomKey = roomKey.Trim();
            }
        }
    }

    public void UpdateRoomOwner(string? ownerUserId, string? ownerNickname)
    {
        lock (_lock)
        {
            RoomOwnerUserId = ownerUserId?.Trim() ?? "";
            RoomOwnerNickname = string.IsNullOrWhiteSpace(ownerNickname) ? "-" : ownerNickname.Trim();
        }
    }

    public ChatMessageFilterContext Snapshot(string? roomKey = null)
    {
        lock (_lock)
        {
            return new ChatMessageFilterContext
            {
                LoginUserId = LoginUserId,
                LoginNickname = LoginNickname,
                RoomOwnerUserId = RoomOwnerUserId,
                RoomOwnerNickname = RoomOwnerNickname,
                RoomKey = string.IsNullOrWhiteSpace(roomKey) ? RoomKey : roomKey.Trim(),
                OutboundTracker = _outboundTracker
            };
        }
    }

    public ChatMessageFilterResult Evaluate(DanmakuItem item)
        => ChatMessageFilter.Evaluate(item, Snapshot(item.RoomKey));

    public ChatMessageClassification Classify(DanmakuItem item)
        => ChatMessageFilter.Classify(item, Snapshot(item.RoomKey));

    public bool ShouldExclude(DanmakuItem item, out string reason)
    {
        var result = Evaluate(item);
        reason = result.Reason;
        return result.Excluded;
    }
}
