namespace LiveAssistant.Models;

public sealed class MovieCatalogEntry
{
    public string MovieId { get; set; } = "";
    public string MovieName { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public int Rank { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class MovieScoreCredit
{
    public long Id { get; set; }
    public string GiftEventId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string GiftName { get; set; } = "";
    public int DiamondCount { get; set; }
    public int Value { get; set; }
    public int Points { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public string? ScoreEventId { get; set; }
    /// <summary>pending / consumed / expired</summary>
    public string Status { get; set; } = "pending";
}

public sealed class MovieScoreEvent
{
    public string EventId { get; set; } = "";
    public string Platform { get; set; } = "douyin";
    public string RoomId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string MovieId { get; set; } = "";
    public string MovieName { get; set; } = "";
    /// <summary>good / bad</summary>
    public string Action { get; set; } = "good";
    public int ScoreDelta { get; set; }
    public int AbsolutePoints { get; set; }
    public List<string> SourceGiftEventIds { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? UploadedAt { get; set; }
    /// <summary>pending / synced / failed</summary>
    public string UploadStatus { get; set; } = "pending";
    public int UploadAttempts { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public long StreamSeq { get; set; }
}

public sealed class MovieScoreTotal
{
    public string MovieId { get; set; } = "";
    public string MovieName { get; set; } = "";
    public long Score { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>本地事件流条目（评分 / 弹幕），供 MaoyanOverlay 轮询。</summary>
public sealed class MovieInteractionStreamItem
{
    public long Seq { get; set; }
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class MovieCatalogUpdateRequest
{
    public List<MovieCatalogUpdateItem> Movies { get; set; } = new();
}

public sealed class MovieCatalogUpdateItem
{
    public string MovieId { get; set; } = "";
    public string MovieName { get; set; } = "";
    public List<string>? Aliases { get; set; }
    public int Rank { get; set; }
}
