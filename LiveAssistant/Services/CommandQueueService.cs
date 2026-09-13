using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 后台命令队列：所有远程控制命令统一入队，由 BackendSyncService 消费并进入 PlaybackCommandQueue。
/// </summary>
public sealed class CommandQueueService
{
    private readonly AdminCommandRepository _repo;

    public CommandQueueService(AdminCommandRepository repo) => _repo = repo;

    public long Enqueue(AdminCommandType type, string? payload = null) =>
        _repo.Enqueue(type, payload);

    public List<AdminCommand> DequeuePending(int limit = 20) =>
        _repo.DequeuePending(limit);
}
