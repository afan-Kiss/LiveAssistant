namespace LiveAssistant.Models;

public enum SongRequestSessionStep
{
    /// <summary>已废弃：仅用于兼容内存中的旧会话，新点歌不再询问歌手。</summary>
    ChooseArtist,
    Confirm
}

public sealed class SongRequestSession
{
    public required string WebRid { get; init; }
    public required string UserId { get; init; }
    public required string Nickname { get; init; }
    public required string Keyword { get; init; }
    public SongRequestSessionStep Step { get; set; }
    public List<SongSearchCandidate> Candidates { get; init; } = new();
    public SongSearchCandidate? Selected { get; set; }
    public DateTime ExpiresAt { get; set; }
    /// <summary>确认已消费，防止重复「确定」双扣积分/双入队。</summary>
    public bool ConfirmConsumed { get; set; }

    public PendingSongKey Key => PendingSongKey.Create(WebRid, UserId);
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
}
