using System.Text.RegularExpressions;
using LiveAssistant.Models;
using LiveAssistant.Utils;

namespace LiveAssistant.Services;

/// <summary>观众消息分类：词云仅展示 NormalChat。</summary>
public enum ChatMessageKind
{
    NormalChat,
    Command,
    BotMessage,
    StreamerMessage,
    SystemMessage
}

public readonly record struct ChatMessageClassification(ChatMessageKind Kind, string Reason)
{
    public bool IsNormalChat => Kind == ChatMessageKind.NormalChat;
}

/// <summary>
/// 观众互动统一过滤：登录号 / 主播号 / 出站回显 / 系统类消息。
/// 所有入口（弹幕 ingress、词云、AI、电影评分）应共用此逻辑。
/// </summary>
public sealed class ChatMessageFilterContext
{
    public string LoginUserId { get; init; } = "";
    public string LoginNickname { get; init; } = "-";
    public string RoomOwnerUserId { get; init; } = "";
    public string RoomOwnerNickname { get; init; } = "-";
    public string? RoomKey { get; init; }
    public OutboundReplyTracker? OutboundTracker { get; init; }
}

public readonly record struct ChatMessageFilterResult(bool Excluded, string Reason)
{
    public static ChatMessageFilterResult Pass => new(false, "");
    public static ChatMessageFilterResult Block(string reason) => new(true, reason);
}

public static partial class ChatMessageFilter
{
    public static ChatMessageFilterResult Evaluate(
        DanmakuItem item,
        ChatMessageFilterContext ctx)
    {
        if (item == null)
        {
            return ChatMessageFilterResult.Block("null_item");
        }

        return Evaluate(
            item.MsgId,
            item.UserId,
            item.Nickname,
            item.Content,
            item.MsgType,
            item.RoomKey ?? ctx.RoomKey,
            ctx);
    }

    public static ChatMessageFilterResult Evaluate(
        string? msgId,
        string? userId,
        string? nickname,
        string? content,
        string? msgType,
        string? roomKey,
        ChatMessageFilterContext ctx)
    {
        if (ctx?.OutboundTracker?.MatchesTrackedMessageId(msgId) == true)
        {
            return ChatMessageFilterResult.Block("outbound_msg_id");
        }

        var uid = (userId ?? "").Trim();
        var loginUid = (ctx?.LoginUserId ?? "").Trim();
        if (uid.Length > 0 && loginUid.Length > 0
            && string.Equals(uid, loginUid, StringComparison.Ordinal))
        {
            return ChatMessageFilterResult.Block("login_user_id");
        }

        var ownerUid = (ctx?.RoomOwnerUserId ?? "").Trim();
        if (uid.Length > 0 && ownerUid.Length > 0
            && string.Equals(uid, ownerUid, StringComparison.Ordinal))
        {
            return ChatMessageFilterResult.Block("room_owner_user_id");
        }

        var nick = (nickname ?? "").Trim();
        var loginNick = NormalizeNick(ctx?.LoginNickname);
        if (nick.Length > 0 && loginNick.Length > 0
            && nick.Equals(loginNick, StringComparison.OrdinalIgnoreCase))
        {
            return ChatMessageFilterResult.Block("login_nickname");
        }

        var ownerNick = NormalizeNick(ctx?.RoomOwnerNickname);
        if (nick.Length > 0 && ownerNick.Length > 0
            && nick.Equals(ownerNick, StringComparison.OrdinalIgnoreCase))
        {
            return ChatMessageFilterResult.Block("room_owner_nickname");
        }

        var text = (content ?? "").Trim();
        var isSelfSender = IsSelfSender(uid, nick, ctx);
        if (isSelfSender && IsOutboundEchoContent(text, roomKey ?? ctx?.RoomKey, ctx))
        {
            return ChatMessageFilterResult.Block("outbound_content");
        }

        if (IsSystemMsgType(msgType))
        {
            return ChatMessageFilterResult.Block("system_msg_type");
        }

        if (isSelfSender && LooksLikeBotReplyTemplate(text))
        {
            return ChatMessageFilterResult.Block("bot_template");
        }

        return ChatMessageFilterResult.Pass;
    }

    private static bool IsSelfSender(string uid, string nick, ChatMessageFilterContext? ctx)
    {
        var loginUid = (ctx?.LoginUserId ?? "").Trim();
        if (uid.Length > 0 && loginUid.Length > 0
            && string.Equals(uid, loginUid, StringComparison.Ordinal))
        {
            return true;
        }

        var ownerUid = (ctx?.RoomOwnerUserId ?? "").Trim();
        if (uid.Length > 0 && ownerUid.Length > 0
            && string.Equals(uid, ownerUid, StringComparison.Ordinal))
        {
            return true;
        }

        var loginNick = NormalizeNick(ctx?.LoginNickname);
        if (nick.Length > 0 && loginNick.Length > 0
            && nick.Equals(loginNick, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ownerNick = NormalizeNick(ctx?.RoomOwnerNickname);
        if (nick.Length > 0 && ownerNick.Length > 0
            && nick.Equals(ownerNick, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 无 user_id / 昵称信号时，允许用出站正文兜底识别机器人回显
        return uid.Length == 0 && nick.Length == 0;
    }

    /// <summary>仅 chat 类观众消息可进词云 / AI / 评分旁路。</summary>
    public static bool IsAudienceChat(DanmakuItem item)
    {
        if (item == null)
        {
            return false;
        }

        var type = (item.MsgType ?? "").Trim();
        return type.Length == 0 || type.Equals("chat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>统一分类：AI / 词云 / 统计共用。</summary>
    public static ChatMessageClassification Classify(DanmakuItem item, ChatMessageFilterContext ctx)
    {
        if (item == null)
        {
            return new ChatMessageClassification(ChatMessageKind.BotMessage, "null_item");
        }

        if (!IsAudienceChat(item))
        {
            return new ChatMessageClassification(ChatMessageKind.SystemMessage, "system_msg_type");
        }

        var filter = Evaluate(item, ctx);
        if (filter.Excluded)
        {
            var kind = filter.Reason switch
            {
                "room_owner_user_id" or "room_owner_nickname" => ChatMessageKind.StreamerMessage,
                _ => ChatMessageKind.BotMessage
            };
            return new ChatMessageClassification(kind, filter.Reason);
        }

        var text = NormalizeCommandInput(item.Content);
        if (IsInteractionCommand(text))
        {
            return new ChatMessageClassification(ChatMessageKind.Command, "interaction_command");
        }

        return new ChatMessageClassification(ChatMessageKind.NormalChat, "");
    }

    public static ChatMessageClassification Classify(DanmakuItem item)
        => Classify(item, new ChatMessageFilterContext());

    /// <summary>词云入口：仅普通观众聊天。</summary>
    public static bool IsNormalChatForWordCloud(DanmakuItem item, ChatMessageFilterContext ctx)
        => Classify(item, ctx).Kind == ChatMessageKind.NormalChat;

    private static string NormalizeCommandInput(string? content)
    {
        var text = SongNameParser.StripMentionPrefix((content ?? "").Trim());
        return text.Trim('。', '.', '!', '！', '?', '？', '~', '～', ' ');
    }

    private static bool IsInteractionCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (MovieInteractionService.TryParseScoreDanmaku(text, out _, out _))
        {
            return true;
        }

        if (SongNameParser.TryParse(text, out _)
            || SkipSongParser.TryParse(text)
            || PointsQueryParser.TryParse(text)
            || SongRequestConfirmParser.IsConfirm(text)
            || SongRequestConfirmParser.IsCancel(text)
            || PlayCommandPattern().IsMatch(text)
            || BanCommandPattern().IsMatch(text)
            || text.Equals("余额", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^播放(?:\s+|[:：]\s*|(?=[^:：\s]))\S", RegexOptions.IgnoreCase)]
    private static partial Regex PlayCommandPattern();

    [GeneratedRegex(@"^(?:禁言|解除禁言|解禁)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BanCommandPattern();

    private static bool IsSystemMsgType(string? msgType)
    {
        var type = (msgType ?? "").Trim().ToLowerInvariant();
        return type is "member" or "gift" or "like" or "digg" or "social" or "enter";
    }

    private static bool IsOutboundEchoContent(string content, string? roomKey, ChatMessageFilterContext? ctx)
    {
        if (string.IsNullOrWhiteSpace(content) || ctx?.OutboundTracker == null)
        {
            return false;
        }

        if (ctx.OutboundTracker.HasRecentOutboundContent(content, roomKey))
        {
            return true;
        }

        // @昵称 正文 — 出站 mention 常见形态
        if (content.StartsWith('@'))
        {
            var body = StripLeadingMention(content);
            if (body.Length > 0 && ctx.OutboundTracker.HasRecentOutboundContent(body, roomKey))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeBotReplyTemplate(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        // 电影评分引导 / 成功反馈 / 欢迎模板（出站后回显兜底）
        if (content.Contains("电影评分", StringComparison.Ordinal)
            || content.Contains("评分机会", StringComparison.Ordinal)
            || content.Contains("》好看 ", StringComparison.Ordinal)
            || content.Contains("》不好看 ", StringComparison.Ordinal)
            || content.Contains("好评成功", StringComparison.Ordinal)
            || content.Contains("差评成功", StringComparison.Ordinal)
            || content.Contains("暂无评分机会", StringComparison.Ordinal)
            || content.Contains("评分资格", StringComparison.Ordinal))
        {
            return true;
        }

        if (content.StartsWith("欢迎", StringComparison.Ordinal)
            && content.Contains("来到直播间", StringComparison.Ordinal))
        {
            return true;
        }

        if (content.StartsWith("感谢", StringComparison.Ordinal)
            && (content.Contains("礼物", StringComparison.Ordinal)
                || content.Contains("送出", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static string StripLeadingMention(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith('@'))
        {
            return text;
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[(space + 1)..].Trim() : "";
    }

    private static string NormalizeNick(string? nickname)
    {
        var nick = (nickname ?? "").Trim();
        return nick.Length == 0 || nick == "-" ? "" : nick;
    }
}
