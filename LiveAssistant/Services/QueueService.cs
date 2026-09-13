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

    public QueueItem Add(QueueItem item)
    {
        lock (_lock)
        {
            item.Id = InsertItem(item);
            item.SortOrder = _waiting.Count;
            _waiting.Add(item);
            NotifyChanged();
            return item;
        }
    }

    public bool Remove(long id)
    {
        lock (_lock)
        {
            var idx = _waiting.FindIndex(x => x.Id == id);
            if (idx < 0)
            {
                return false;
            }

            _waiting.RemoveAt(idx);
            DeleteItem(id);
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
                DeleteItem(item.Id);
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
                DeleteItem(_nowPlaying.Id);
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
            _nowPlaying = next;
            NotifyChanged();
            return next;
        }
    }

    public void SetNowPlaying(QueueItem? item)
    {
        lock (_lock)
        {
            _nowPlaying = item;
            NotifyChanged();
        }
    }

    public void FinishCurrent()
    {
        lock (_lock)
        {
            if (_nowPlaying != null)
            {
                DeleteItem(_nowPlaying.Id);
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
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, user_id, nickname, song_name, artist, song_id, hash, is_random, sort_order, created_at
                FROM queue_items
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
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO queue_items (user_id, nickname, song_name, artist, song_id, hash, is_random, sort_order, created_at)
            VALUES ($uid, $nick, $song, $artist, $sid, $hash, $random, $order, $created)
            """;
        cmd.Parameters.AddWithValue("$uid", item.UserId);
        cmd.Parameters.AddWithValue("$nick", item.Nickname);
        cmd.Parameters.AddWithValue("$song", item.SongName);
        cmd.Parameters.AddWithValue("$artist", item.Artist);
        cmd.Parameters.AddWithValue("$sid", item.SongId);
        cmd.Parameters.AddWithValue("$hash", item.Hash);
        cmd.Parameters.AddWithValue("$random", item.IsRandom ? 1 : 0);
        cmd.Parameters.AddWithValue("$order", item.SortOrder);
        cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(idCmd.ExecuteScalar() ?? 0L);
    }

    private void DeleteItem(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM queue_items WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private void ReindexWaiting()
    {
        for (var i = 0; i < _waiting.Count; i++)
        {
            _waiting[i].SortOrder = i;
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE queue_items SET sort_order = $order WHERE id = $id";
            cmd.Parameters.AddWithValue("$order", i);
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
            CreatedAt = DateTime.TryParse(reader.GetString(9), out var dt) ? dt : DateTime.Now
        };
    }

    private void NotifyChanged() => QueueChanged?.Invoke();
}
