using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveAssistant.Services;

/// <summary>
/// 弹幕 msg_id 去重：内存 TTL + 本地文件，覆盖 reconnect / 进程重启后的重放。
/// </summary>
public sealed class DanmakuDeduplicator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly TimeSpan _ttl;
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);

    public DanmakuDeduplicator(string? dataDirectory = null, TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(30);
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
            _path = Path.Combine(dataDirectory, "danmaku_dedupe.json");
            LoadFromDisk();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _seen.Count;
            }
        }
    }

    /// <summary>
    /// 未见过则登记并返回 true；TTL 内已见过返回 false。
    /// 空 msg_id 无法去重，返回 true（放行）。
    /// </summary>
    public bool TryAdmit(string? msgId, DateTime? utcNow = null)
    {
        var id = msgId?.Trim() ?? "";
        if (id.Length == 0)
        {
            return true;
        }

        var now = utcNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            PruneExpiredLocked(now);
            if (_seen.TryGetValue(id, out var seenAt) && now - seenAt < _ttl)
            {
                return false;
            }

            _seen[id] = now;
            PersistLocked(now);
            return true;
        }
    }

    public bool Contains(string? msgId, DateTime? utcNow = null)
    {
        var id = msgId?.Trim() ?? "";
        if (id.Length == 0)
        {
            return false;
        }

        var now = utcNow ?? DateTime.UtcNow;
        lock (_gate)
        {
            PruneExpiredLocked(now);
            return _seen.TryGetValue(id, out var seenAt) && now - seenAt < _ttl;
        }
    }

    private void LoadFromDisk()
    {
        if (_path == null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<DedupeFile>(json, JsonOptions);
            if (file?.Ids == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var (id, atText) in file.Ids)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (!DateTime.TryParse(atText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                {
                    continue;
                }

                if (now - at.ToUniversalTime() < _ttl)
                {
                    _seen[id.Trim()] = at.ToUniversalTime();
                }
            }
        }
        catch
        {
            // ignore corrupt file
        }
    }

    private void PersistLocked(DateTime now)
    {
        if (_path == null)
        {
            return;
        }

        try
        {
            PruneExpiredLocked(now);
            var file = new DedupeFile();
            foreach (var (id, at) in _seen)
            {
                file.Ids[id] = at.ToString("O");
            }

            var json = JsonSerializer.Serialize(file, JsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, _path, overwrite: true);
            File.Delete(tmp);
        }
        catch
        {
            // ignore persist failures
        }
    }

    private void PruneExpiredLocked(DateTime now)
    {
        if (_seen.Count == 0)
        {
            return;
        }

        var expired = _seen
            .Where(kv => now - kv.Value >= _ttl)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired)
        {
            _seen.Remove(key);
        }
    }

    private sealed class DedupeFile
    {
        [JsonPropertyName("ids")]
        public Dictionary<string, string> Ids { get; set; } = new(StringComparer.Ordinal);
    }
}
