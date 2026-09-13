namespace LiveAssistant.Models;

public sealed class GiftEvent
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string GiftId { get; set; } = "";
    public string GiftName { get; set; } = "";
    public int Count { get; set; }
    public int Value { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
