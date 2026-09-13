using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// AI 回复预留接口，暂不接入模型。
/// </summary>
public interface IAIReplyService
{
    Task<string?> TryReplyAsync(DanmakuItem item, CancellationToken ct = default);
}

public sealed class NullAIReplyService : IAIReplyService
{
    public Task<string?> TryReplyAsync(DanmakuItem item, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
