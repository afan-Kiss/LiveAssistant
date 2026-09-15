namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 按稳定 UserId 保存近期用户发言与主播回复（TTL + 最大用户数 LRU）。
/// </summary>
public sealed class UserConversationContext
{
    private readonly object _gate = new();
    private readonly Dictionary<string, UserBucket> _users = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private int _userMax;
    private int _hostMax;
    private int _maxUsers;
    private TimeSpan _ttl;

    private sealed class UserBucket
    {
        public DateTime LastTouchUtc { get; set; } = DateTime.UtcNow;
        public LinkedListNode<string>? LruNode { get; set; }
        public LinkedList<(string Role, string Content, DateTime At)> Messages { get; } = new();
    }

    public UserConversationContext(
        int userMax = 8,
        int hostMax = 4,
        int ttlMinutes = 15,
        int maxUsers = 2000)
    {
        Configure(userMax, hostMax, ttlMinutes, maxUsers);
    }

    public void Configure(int userMax, int hostMax, int ttlMinutes, int maxUsers = 2000)
    {
        lock (_gate)
        {
            _userMax = Math.Clamp(userMax, 1, 40);
            _hostMax = Math.Clamp(hostMax, 1, 20);
            _ttl = TimeSpan.FromMinutes(Math.Clamp(ttlMinutes, 1, 180));
            _maxUsers = Math.Clamp(maxUsers, 16, 50_000);
            EvictOverCapacity_NoLock();
        }
    }

    /// <summary>当前跟踪的用户数（诊断/压测）。</summary>
    public int TrackedUserCount
    {
        get { lock (_gate) return _users.Count; }
    }

    public int MaxUsers
    {
        get { lock (_gate) return _maxUsers; }
    }

    public void AddUserMessage(string userId, string content)
        => Add(userId, "user", content);

    public void AddHostReply(string userId, string content)
        => Add(userId, "assistant", content);

    public void Add(string userId, string role, string content)
    {
        userId = (userId ?? "").Trim();
        content = (content ?? "").Trim();
        if (userId.Length == 0 || content.Length == 0)
        {
            return;
        }

        role = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
        lock (_gate)
        {
            PurgeExpired_NoLock(DateTime.UtcNow);
            if (!_users.TryGetValue(userId, out var bucket))
            {
                bucket = new UserBucket();
                _users[userId] = bucket;
                bucket.LruNode = _lru.AddLast(userId);
                EvictOverCapacity_NoLock();
            }
            else
            {
                TouchLru_NoLock(bucket, userId);
            }

            bucket.LastTouchUtc = DateTime.UtcNow;
            bucket.Messages.AddLast((role, content, DateTime.UtcNow));
            TrimBucket_NoLock(bucket);
        }
    }

    public IReadOnlyList<(string Role, string Content)> GetMessages(string userId)
    {
        userId = (userId ?? "").Trim();
        if (userId.Length == 0)
        {
            return Array.Empty<(string, string)>();
        }

        lock (_gate)
        {
            PurgeExpired_NoLock(DateTime.UtcNow);
            if (!_users.TryGetValue(userId, out var bucket))
            {
                return Array.Empty<(string, string)>();
            }

            TouchLru_NoLock(bucket, userId);
            bucket.LastTouchUtc = DateTime.UtcNow;
            return bucket.Messages.Select(m => (m.Role, m.Content)).ToList();
        }
    }

    public void Clear(string? userId = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                _users.Clear();
                _lru.Clear();
            }
            else
            {
                RemoveUser_NoLock(userId.Trim());
            }
        }
    }

    private void TouchLru_NoLock(UserBucket bucket, string userId)
    {
        if (bucket.LruNode != null)
        {
            _lru.Remove(bucket.LruNode);
        }

        bucket.LruNode = _lru.AddLast(userId);
    }

    private void EvictOverCapacity_NoLock()
    {
        while (_users.Count > _maxUsers && _lru.First != null)
        {
            var oldest = _lru.First.Value;
            RemoveUser_NoLock(oldest);
        }
    }

    private void RemoveUser_NoLock(string userId)
    {
        if (!_users.TryGetValue(userId, out var bucket))
        {
            return;
        }

        if (bucket.LruNode != null)
        {
            _lru.Remove(bucket.LruNode);
        }

        _users.Remove(userId);
    }

    private void TrimBucket_NoLock(UserBucket bucket)
    {
        var userCount = 0;
        var hostCount = 0;
        foreach (var m in bucket.Messages)
        {
            if (m.Role == "assistant") hostCount++;
            else userCount++;
        }

        while (bucket.Messages.Count > 0 && (userCount > _userMax || hostCount > _hostMax || bucket.Messages.Count > _userMax + _hostMax))
        {
            var first = bucket.Messages.First!.Value;
            bucket.Messages.RemoveFirst();
            if (first.Role == "assistant") hostCount--;
            else userCount--;
        }
    }

    private void PurgeExpired_NoLock(DateTime now)
    {
        foreach (var key in _users.Keys.ToList())
        {
            if (now - _users[key].LastTouchUtc > _ttl)
            {
                RemoveUser_NoLock(key);
            }
        }
    }
}
