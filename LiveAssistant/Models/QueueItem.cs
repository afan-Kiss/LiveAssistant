namespace LiveAssistant.Models;

public sealed class QueueItem
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string SongName { get; set; } = "";
    public string Artist { get; set; } = "";
    public string SongId { get; set; } = "";
    public string Hash { get; set; } = "";
    public string? PlayUrl { get; set; }
    public bool IsRandom { get; set; }
    public int SortOrder { get; set; }
    public QueueItemStatus Status { get; set; } = QueueItemStatus.Waiting;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }

    public string DisplayLine => IsRandom
        ? $"🎵 {SongName} - {Artist}"
        : $"{Nickname} - {SongName}";

    public static string StatusToDb(QueueItemStatus status) => status switch
    {
        QueueItemStatus.Waiting => "waiting",
        QueueItemStatus.Playing => "playing",
        QueueItemStatus.Finished => "finished",
        QueueItemStatus.Deleted => "deleted",
        _ => "waiting"
    };

    public static QueueItemStatus StatusFromDb(string? value) => value switch
    {
        "playing" => QueueItemStatus.Playing,
        "finished" => QueueItemStatus.Finished,
        "deleted" => QueueItemStatus.Deleted,
        _ => QueueItemStatus.Waiting
    };
}
