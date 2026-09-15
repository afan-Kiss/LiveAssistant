using System.Security.Cryptography;
using System.Text;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 防止 AI 在短时间窗口内重复说同一句（如连续「感谢支持」）。
/// 默认保留最近 20 条输出 hash，窗口 5 分钟。
/// </summary>
public sealed class AiReplyDuplicateGuard
{
    private readonly object _gate = new();
    private readonly LinkedList<(string Hash, DateTime Utc)> _recent = new();
    private readonly int _capacity;
    private readonly TimeSpan _window;

    public AiReplyDuplicateGuard(int capacity = 20, TimeSpan? window = null)
    {
        _capacity = Math.Clamp(capacity, 4, 100);
        _window = window ?? TimeSpan.FromMinutes(5);
    }

    public int Count
    {
        get { lock (_gate) { Purge_NoLock(DateTime.UtcNow); return _recent.Count; } }
    }

    /// <summary>
    /// 若重复则返回 true（应跳过 TTS）；否则登记并返回 false。
    /// </summary>
    public bool IsDuplicate(string? replyText)
    {
        var normalized = Normalize(replyText);
        if (normalized.Length < 2)
        {
            return false;
        }

        var hash = Hash(normalized);
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            Purge_NoLock(now);
            foreach (var item in _recent)
            {
                if (item.Hash == hash)
                {
                    return true;
                }
            }

            _recent.AddLast((hash, now));
            while (_recent.Count > _capacity)
            {
                _recent.RemoveFirst();
            }

            return false;
        }
    }

    public void Clear()
    {
        lock (_gate) _recent.Clear();
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch) || ch is '，' or ',' or '。' or '！' or '!' or '？' or '?' or '、')
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private void Purge_NoLock(DateTime now)
    {
        while (_recent.Count > 0 && now - _recent.First!.Value.Utc > _window)
        {
            _recent.RemoveFirst();
        }
    }

    private static string Hash(string normalized)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}
