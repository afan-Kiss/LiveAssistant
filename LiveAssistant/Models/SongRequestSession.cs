namespace LiveAssistant.Models;

public enum SongRequestSessionStep
{
    /// <summary>已废弃：旧版多歌手询问流程，内存会话迁移时自动转为 Confirm。</summary>
    ChooseArtist,
    Confirm
}

public sealed class SongRequestSession
{
    public required string UserId { get; init; }
    public required string Nickname { get; init; }
    public required string Keyword { get; init; }
    public SongRequestSessionStep Step { get; set; }
    public List<SongSearchCandidate> Candidates { get; init; } = new();
    public SongSearchCandidate? Selected { get; set; }
    public DateTime ExpiresAt { get; set; }
    /// <summary>确认已消费，防止重复「确定」双扣积分/双入队。</summary>
    public bool ConfirmConsumed { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
}
