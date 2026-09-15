using System.Text.Json;
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

    [JsonPropertyName("mention_count")]
    public int MentionCount { get; set; }
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

    [JsonPropertyName("login_hint")]
    public string? LoginHint { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("collect_sessions")]
    public int CollectSessions { get; set; }

    [JsonPropertyName("write_gate")]
    public JsonElement? WriteGate { get; set; }
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

    [JsonPropertyName("room_id")]
    public string? RoomId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("owner")]
    public DouyinUser? Owner { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("user_count")]
    public int UserCount { get; set; }

    [JsonPropertyName("raw")]
    public DouyinRoomRawData? Raw { get; set; }
}

public sealed class DouyinRoomRawData
{
    [JsonPropertyName("cookie")]
    public string? Cookie { get; set; }
}

public sealed class DouyinCookieStatusData
{
    [JsonPropertyName("active")]
    public string? Active { get; set; }

    [JsonPropertyName("login_ok")]
    public bool LoginOk { get; set; }

    [JsonPropertyName("login_hint")]
    public string? LoginHint { get; set; }
}

public sealed class DouyinCollectSession
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("web_rid")]
    public string? WebRid { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }
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

    [JsonPropertyName("总数")]
    public int Total { get; set; }
}

public sealed class MoeEverydayRecommendResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("data")]
    public MoeEverydayRecommendData? Data { get; set; }
}

public sealed class MoeEverydayRecommendData
{
    [JsonPropertyName("song_list")]
    public List<MoeEverydaySong>? SongList { get; set; }
}

public sealed class MoeEverydaySong
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("ori_audio_name")]
    public string? OriAudioName { get; set; }

    [JsonPropertyName("songname")]
    public string? SongName { get; set; }

    [JsonPropertyName("author_name")]
    public string? AuthorName { get; set; }

    [JsonPropertyName("album_id")]
    public string? AlbumId { get; set; }

    [JsonPropertyName("album_audio_id")]
    public JsonElement AlbumAudioId { get; set; }

    [JsonPropertyName("mixsongid")]
    public JsonElement MixSongId { get; set; }

    [JsonPropertyName("time_length")]
    public int TimeLength { get; set; }
}

public sealed class KugouVipClaimData
{
    [JsonPropertyName("claimed")]
    public bool Claimed { get; set; }

    [JsonPropertyName("upgraded")]
    public bool Upgraded { get; set; }

    [JsonPropertyName("already_claimed")]
    public bool AlreadyClaimed { get; set; }

    [JsonPropertyName("vip_label")]
    public string? VipLabel { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public sealed class KugouSidecarSession
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("userid")]
    public string? UserId { get; set; }

    [JsonPropertyName("vip_token")]
    public string? VipToken { get; set; }

    [JsonPropertyName("vip_type")]
    public string? VipType { get; set; }

    [JsonPropertyName("dfid")]
    public string? Dfid { get; set; }

    [JsonPropertyName("extra")]
    public Dictionary<string, string>? Extra { get; set; }
}

public sealed class KugouLoginStatusData
{
    [JsonPropertyName("logged_in")]
    public bool LoggedIn { get; set; }

    [JsonPropertyName("userid")]
    public string? UserId { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("vip_label")]
    public string? VipLabel { get; set; }

    [JsonPropertyName("vip_type")]
    public string? VipType { get; set; }

    [JsonPropertyName("has_vip_token")]
    public bool HasVipToken { get; set; }

    [JsonPropertyName("vip_end")]
    public string? VipEnd { get; set; }
}

public sealed class KugouSongItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("歌曲ID")]
    public string? SongId { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("album_id")]
    public string? AlbumId { get; set; }

    [JsonPropertyName("album_audio_id")]
    public long AlbumAudioId { get; set; }

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

    [JsonPropertyName("is_preview")]
    public bool IsPreview { get; set; }

    [JsonPropertyName("time_length")]
    public int TimeLength { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("歌曲名称")]
    public string? SongName { get; set; }

    [JsonPropertyName("歌手名称")]
    public string? Artist { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("歌曲ID")]
    public string? SongId { get; set; }
}
