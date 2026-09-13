namespace LiveAssistant.Models;

public sealed class GiftRule
{
    public long Id { get; set; }
    public string GiftName { get; set; } = "";
    public int Points { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
