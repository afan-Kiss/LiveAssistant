namespace LiveAssistant.Models;

/// <summary>点歌待确认会话键：按直播间隔离，避免跨房间串状态。</summary>
public readonly record struct PendingSongKey(string WebRid, string UserId)
{
    public static PendingSongKey Create(string? webRid, string? userId)
        => new((webRid ?? "").Trim(), (userId ?? "").Trim());

    public bool IsValid => !string.IsNullOrWhiteSpace(UserId);

    public string StorageKey => $"{WebRid}\u001f{UserId}";

    public override string ToString() => $"webRid={WebRid}|userId={UserId}";
}
