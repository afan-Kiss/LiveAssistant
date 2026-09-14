namespace LiveAssistant.Models;

public sealed class GiftRule
{
    public long Id { get; set; }
    public string GiftId { get; set; } = "";
    public string GiftName { get; set; } = "";
    public int Points { get; set; }
    /// <summary>点歌次数：0=不赠送，正数=每次礼物赠送次数，-1=无限点歌</summary>
    public int SongPermissionCount { get; set; }
    public bool AllowSongRequest { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public int EffectiveSongPermissionCount()
    {
        if (SongPermissionCount != 0)
        {
            return SongPermissionCount;
        }
        return AllowSongRequest ? 1 : 0;
    }
}
