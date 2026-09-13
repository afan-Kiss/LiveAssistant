namespace LiveAssistant.Services;

public sealed class SystemMessageService
{
    private readonly int _maxLines;
    private readonly List<string> _messages = new();
    private readonly object _lock = new();

    public event Action<string>? MessageAdded;

    public SystemMessageService(int maxLines = 200)
    {
        _maxLines = maxLines;
    }

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_lock)
            {
                return _messages.ToList();
            }
        }
    }

    public void Add(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_lock)
        {
            _messages.Add(line);
            while (_messages.Count > _maxLines)
            {
                _messages.RemoveAt(0);
            }
        }
        MessageAdded?.Invoke(line);
    }
}
