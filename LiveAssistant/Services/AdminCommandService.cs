using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

public sealed class AdminCommandService
{
    private readonly AdminCommandRepository _repo;

    public AdminCommandService(AdminCommandRepository repo)
    {
        _repo = repo;
    }

    public long Enqueue(AdminCommandType type, string? payload = null) =>
        _repo.Enqueue(type, payload);

    public List<AdminCommand> DequeuePending(int limit = 20) =>
        _repo.DequeuePending(limit);
}
