using System.Text.Json.Serialization;

namespace LiveAssistant.Models;

public sealed class DouyinEnvelope<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

public sealed class DouyinDanmakuFeedData
{
    [JsonPropertyName("items")]
    public List<DouyinDanmakuMessage>? Items { get; set; }

    [JsonPropertyName("message_count")]
    public int MessageCount { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }
}

public sealed class DouyinDanmakuMessage
{
    [JsonPropertyName("msg_id")]
    public string? MsgId { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("msg_type")]
    public string? MsgType { get; set; }

    [JsonPropertyName("user")]
    public DouyinUser? User { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

public sealed class DouyinUser
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }
}

public sealed class DouyinHealthData
{
    [JsonPropertyName("login_ok")]
    public bool LoginOk { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("collect_sessions")]
    public int CollectSessions { get; set; }
}

public sealed class DouyinGiftFeedData
{
    [JsonPropertyName("items")]
    public List<DouyinGiftMessage>? Items { get; set; }

    [JsonPropertyName("gift_count")]
    public int GiftCount { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }
}

public sealed class DouyinGiftMessage
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("user")]
    public DouyinUser? User { get; set; }

    [JsonPropertyName("gift_id")]
    public string? GiftId { get; set; }

    [JsonPropertyName("gift_name")]
    public string? GiftName { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("value")]
    public int Value { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }
}

public sealed class DouyinLookupData
{
    [JsonPropertyName("user")]
    public DouyinUser? User { get; set; }
}

public sealed class DouyinRoomData
{
    [JsonPropertyName("web_rid")]
    public string? WebRid { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }
}

public sealed class KugouResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

public sealed class KugouSearchData
{
    [JsonPropertyName("歌单")]
    public List<KugouSongItem>? Songs { get; set; }
}

public sealed class KugouSongItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("歌曲ID")]
    public string? SongId { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("歌曲名称")]
    public string? SongName { get; set; }

    [JsonPropertyName("歌手名称")]
    public string? Artist { get; set; }

    [JsonPropertyName("歌曲长度")]
    public int Duration { get; set; }
}

public sealed class KugouUrlData
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("歌曲名称")]
    public string? SongName { get; set; }

    [JsonPropertyName("歌手名称")]
    public string? Artist { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("歌曲ID")]
    public string? SongId { get; set; }
}
