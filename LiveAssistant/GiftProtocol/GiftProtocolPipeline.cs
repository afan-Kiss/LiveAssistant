using Douyin.Live;
using LiveAssistant.Models;

namespace LiveAssistant.GiftProtocol;

/// <summary>
/// 礼物解析门面：PushFrame/GiftMessage → ComboTracker → 标准 GiftEvent。
/// </summary>
public sealed class GiftProtocolPipeline
{
    private readonly ComboTracker _combo;

    public GiftProtocolPipeline(TimeSpan? comboTimeout = null)
    {
        _combo = new ComboTracker(comboTimeout);
    }

    public ComboTracker Combo => _combo;

    /// <summary>
    /// 解析 PushFrame 原始字节并经过连击合并输出最终礼物事件。
    /// protobuf 解析失败时返回空列表（不抛出）。
    /// </summary>
    public IReadOnlyList<GiftEvent> ProcessPushFrame(byte[] pushFrameBytes, DateTime? utcNow = null)
    {
        var parsed = WebcastGiftParser.ParseGiftMessages(pushFrameBytes);
        if (!parsed.Success)
        {
            return Array.Empty<GiftEvent>();
        }

        var emitted = new List<GiftEvent>();
        foreach (var gift in parsed.Gifts)
        {
            emitted.AddRange(_combo.Process(gift, utcNow));
        }

        return emitted;
    }

    public IReadOnlyList<GiftEvent> ProcessGiftMessage(GiftMessage message, DateTime? utcNow = null)
        => _combo.Process(message, utcNow);

    public IReadOnlyList<GiftEvent> FlushExpired(DateTime? utcNow = null)
        => _combo.FlushExpired(utcNow);

    public IReadOnlyList<GiftEvent> FlushAll()
        => _combo.FlushAll();

    public void Reset()
        => _combo.Clear();
}
