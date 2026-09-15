using LiveAssistant.Database;
using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Services;

public sealed class QueueService
{
    private readonly AppDatabase _db;
    private readonly object _lock = new();
    private QueueItem? _nowPlaying;
    private readonly List<QueueItem> _waiting = new();

    public event Action? QueueChanged;

    public QueueService(AppDatabase db)
    {
        _db = db;
        LoadFromDatabase();
    }

    public QueueItem? NowPlaying
    {
        get { lock (_lock) return _nowPlaying; }
    }

    public IReadOnlyList<QueueItem> Waiting
    {
        get { lock (_lock) return _waiting.ToList(); }
    }

    public int WaitingCount
    {
        get { lock (_lock) return _waiting.Count; }
    }

    public int TotalQueueCount
    {
        get { lock (_lock) return _waiting.Count + (_nowPlaying != null ? 1 : 0); }
    }

    public int GetAheadCount(long queueItemId)
    {
        lock (_lock)
        {
            if (_nowPlaying?.Id == queueItemId)
            {
                return 0;
            }

            // 随机补位不计入用户排队等待数
            var ahead = _nowPlaying != null && !_nowPlaying.IsRandom ? 1 : 0;
            var idx = _waiting.FindIndex(x => x.Id == queueItemId);
            if (idx < 0)
            {
                return ahead;
            }

            return ahead + idx;
        }
    }

    public IReadOnlyList<QueueItem> GetAllItems()
    {
        lock (_lock)
        {
            var list = new List<QueueItem>(_waiting);
            if (_nowPlaying != null)
            {
                list.Insert(0, _nowPlaying);
            }
            return list;
        }
    }

    public QueueItem? GetItem(long id)
    {
        lock (_lock)
        {
            if (_nowPlaying?.Id == id)
            {
                return _nowPlaying;
            }
            return _waiting.FirstOrDefault(x => x.Id == id);
        }
    }

    public bool PinToTop(long id)
    {
        lock (_lock)
        {
            var idx = _waiting.FindIndex(x => x.Id == id);
            if (idx <= 0)
            {
                return idx == 0;
            }

            var item = _waiting[idx];
            _waiting.RemoveAt(idx);
            _waiting.Insert(0, item);
            ReindexWaiting();
            NotifyChanged();
            return true;
        }
    }

    public QueueItem Add(QueueItem item) => AddWithPriority(item, 0);

    public QueueItem AddWithPriority(QueueItem item, int queuePriority)
        => TryAddWithPriority(item, queuePriority, maxSize: 0, bypassCapacity: true, out var added)
            ? added!
            : throw new InvalidOperationException("队列入队失败");

    /// <summary>
    /// 在锁内原子检查容量并插入。maxSize&lt;=0 或 bypassCapacity 时不限容量（管理员特权/内部路径）。
    /// </summary>
    public bool TryAddWithPriority(
        QueueItem item,
        int queuePriority,
        int maxSize,
        bool bypassCapacity,
        out QueueItem? added)
    {
        lock (_lock)
        {
            if (!bypassCapacity && maxSize > 0 && _waiting.Count >= maxSize)
            {
                added = null;
                return false;
            }

            item.Status = QueueItemStatus.Waiting;
            item.Id = InsertItem(item);
            if (queuePriority <= 0 || _waiting.Count == 0)
            {
                item.SortOrder = _waiting.Count;
                _waiting.Add(item);
            }
            else
            {
                var insertIndex = Math.Max(0, _waiting.Count - (queuePriority / 5 + 1));
                insertIndex = Math.Min(insertIndex, _waiting.Count);
                _waiting.Insert(insertIndex, item);
                ReindexWaiting();
            }

            NotifyChanged();
            added = item;
            return true;
        }
    }

    public QueueItem BeginPlaying(QueueItem item)
    {
        lock (_lock)
        {
            if (_nowPlaying != null && _nowPlaying.Id > 0)
            {
                UpdateStatus(_nowPlaying.Id, QueueItemStatus.Finished);
            }

            item.Status = QueueItemStatus.Playing;
            if (item.Id <= 0)
            {
                item.Id = InsertItem(item);
            }
            else
            {
                UpdateStatus(item.Id, QueueItemStatus.Playing);
            }

            _nowPlaying = item;
            NotifyChanged();
            return item;
        }
    }

    public bool Remove(long id)
    {
        lock (_lock)
        {
            if (_nowPlaying?.Id == id)
            {
                UpdateStatus(id, QueueItemStatus.Deleted);
                _nowPlaying = null;
                NotifyChanged();
                return true;
            }

            var idx = _waiting.FindIndex(x => x.Id == id);
            if (idx < 0)
            {
                return false;
            }

            _waiting.RemoveAt(idx);
            UpdateStatus(id, QueueItemStatus.Deleted);
            ReindexWaiting();
            NotifyChanged();
            return true;
        }
    }

    public void ClearWaiting()
    {
        lock (_lock)
        {
            foreach (var item in _waiting)
            {
                UpdateStatus(item.Id, QueueItemStatus.Deleted);
            }
            _waiting.Clear();
            NotifyChanged();
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            if (_nowPlaying != null)
            {
                UpdateStatus(_nowPlaying.Id, QueueItemStatus.Deleted);
                _nowPlaying = null;
            }
            ClearWaiting();
        }
    }

    public QueueItem? DequeueNext()
    {
        lock (_lock)
        {
            if (_waiting.Count == 0)
            {
                return null;
            }

            var next = _waiting[0];
            _waiting.RemoveAt(0);
            ReindexWaiting();
            next.Status = QueueItemStatus.Playing;
            UpdateStatus(next.Id, QueueItemStatus.Playing);
            _nowPlaying = next;
            NotifyChanged();
            return next;
        }
    }

    public void SetNowPlaying(QueueItem? item)
    {
        lock (_lock)
        {
            if (item != null)
            {
                item.Status = QueueItemStatus.Playing;
                if (item.Id > 0)
                {
                    UpdateStatus(item.Id, QueueItemStatus.Playing);
                }
            }
            _nowPlaying = item;
            NotifyChanged();
        }
    }

    /// <summary>播放前解析到最新曲目信息后，同步正在播放项的展示字段并持久化。</summary>
    public void SyncNowPlayingMetadata(QueueItem item)
    {
        lock (_lock)
        {
            if (_nowPlaying == null || _nowPlaying.Id != item.Id)
            {
                return;
            }

            UpdateItemFields(item);
            NotifyChanged();
        }
    }

    public void FinishCurrent()
    {
        lock (_lock)
        {
            if (_nowPlaying != null)
            {
                UpdateStatus(_nowPlaying.Id, QueueItemStatus.Finished);
                _nowPlaying = null;
            }
            NotifyChanged();
        }
    }

    private void LoadFromDatabase()
    {
        lock (_lock)
        {
            _waiting.Clear();
            _nowPlaying = null;

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, user_id, nickname, song_name, artist, song_id, hash, is_random, sort_order, status, created_at, updated_at
                FROM queue_items
                WHERE status = 'waiting'
                ORDER BY sort_order ASC, id ASC
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _waiting.Add(ReadItem(reader));
            }
        }
    }

    private long InsertItem(QueueItem item)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO queue_items (user_id, nickname, song_name, artist, song_id, hash, is_random, sort_order, status, created_at, updated_at)
            VALUES ($uid, $nick, $song, $artist, $sid, $hash, $random, $order, $status, $created, $updated)
            """;
        cmd.Parameters.AddWithValue("$uid", item.UserId);
        cmd.Parameters.AddWithValue("$nick", item.Nickname);
        cmd.Parameters.AddWithValue("$song", item.SongName);
        cmd.Parameters.AddWithValue("$artist", item.Artist);
        cmd.Parameters.AddWithValue("$sid", item.SongId);
        cmd.Parameters.AddWithValue("$hash", item.Hash);
        cmd.Parameters.AddWithValue("$random", item.IsRandom ? 1 : 0);
        cmd.Parameters.AddWithValue("$order", item.SortOrder);
        cmd.Parameters.AddWithValue("$status", QueueItem.StatusToDb(item.Status));
        cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", now);
        cmd.ExecuteNonQuery();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(idCmd.ExecuteScalar() ?? 0L);
    }

    private void UpdateStatus(long id, QueueItemStatus status)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE queue_items
            SET status = $status, updated_at = $updated
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$status", QueueItem.StatusToDb(status));
        cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private void UpdateItemFields(QueueItem item)
    {
        if (item.Id <= 0)
        {
            return;
        }

        item.UpdatedAt = DateTime.Now;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE queue_items
            SET song_name = $song, artist = $artist, song_id = $sid, hash = $hash,
                updated_at = $updated
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$song", item.SongName);
        cmd.Parameters.AddWithValue("$artist", item.Artist);
        cmd.Parameters.AddWithValue("$sid", item.SongId);
        cmd.Parameters.AddWithValue("$hash", item.Hash);
        cmd.Parameters.AddWithValue("$updated", item.UpdatedAt.Value.ToString("O"));
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.ExecuteNonQuery();
    }

    private void ReindexWaiting()
    {
        for (var i = 0; i < _waiting.Count; i++)
        {
            _waiting[i].SortOrder = i;
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE queue_items SET sort_order = $order, updated_at = $updated WHERE id = $id";
            cmd.Parameters.AddWithValue("$order", i);
            cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$id", _waiting[i].Id);
            cmd.ExecuteNonQuery();
        }
    }

    private static QueueItem ReadItem(SqliteDataReader reader)
    {
        return new QueueItem
        {
            Id = reader.GetInt64(0),
            UserId = reader.GetString(1),
            Nickname = reader.GetString(2),
            SongName = reader.GetString(3),
            Artist = reader.GetString(4),
            SongId = reader.GetString(5),
            Hash = reader.GetString(6),
            IsRandom = reader.GetInt64(7) == 1,
            SortOrder = (int)reader.GetInt64(8),
            Status = QueueItem.StatusFromDb(reader.IsDBNull(9) ? null : reader.GetString(9)),
            CreatedAt = DateTime.TryParse(reader.GetString(10), out var dt) ? dt : DateTime.Now,
            UpdatedAt = reader.IsDBNull(11) ? null : DateTime.TryParse(reader.GetString(11), out var u) ? u : null
        };
    }

    private void NotifyChanged() => QueueChanged?.Invoke();
}
