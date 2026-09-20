using System.Text.Json;
using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

/// <summary>
/// 电影互动评分独立账本：积分池 / 评分流水 / 总分 / 目录 / 本地事件流。
/// 不复用 points_ledger。
/// </summary>
public sealed class MovieInteractionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppDatabase _db;

    public MovieInteractionRepository(AppDatabase db)
    {
        _db = db;
    }

    public bool CreditExistsByGiftEventId(string giftEventId)
    {
        if (string.IsNullOrWhiteSpace(giftEventId))
        {
            return false;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM movie_score_credits WHERE gift_event_id = $eid";
        cmd.Parameters.AddWithValue("$eid", giftEventId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public bool TryInsertCredit(MovieScoreCredit credit)
    {
        if (string.IsNullOrWhiteSpace(credit.GiftEventId) || string.IsNullOrWhiteSpace(credit.UserId))
        {
            return false;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO movie_score_credits (
                gift_event_id, user_id, nickname, gift_name, diamond_count, value,
                points, created_at, expires_at, status)
            VALUES (
                $eid, $uid, $nick, $gname, $diamond, $value,
                $points, $created, $expires, 'pending')
            """;
        cmd.Parameters.AddWithValue("$eid", credit.GiftEventId);
        cmd.Parameters.AddWithValue("$uid", credit.UserId);
        cmd.Parameters.AddWithValue("$nick", credit.Nickname ?? "");
        cmd.Parameters.AddWithValue("$gname", credit.GiftName ?? "");
        cmd.Parameters.AddWithValue("$diamond", credit.DiamondCount);
        cmd.Parameters.AddWithValue("$value", credit.Value);
        cmd.Parameters.AddWithValue("$points", credit.Points);
        cmd.Parameters.AddWithValue("$created", credit.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$expires", credit.ExpiresAt.ToString("O"));
        try
        {
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public int ExpirePendingCredits(DateTime now)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE movie_score_credits
            SET status = 'expired'
            WHERE status = 'pending' AND expires_at <= $now
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    public int CountPendingCredits(DateTime? now = null)
    {
        var at = (now ?? DateTime.Now).ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM movie_score_credits
            WHERE status = 'pending' AND expires_at > $now
            """;
        cmd.Parameters.AddWithValue("$now", at);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountPendingUploads()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM movie_score_events
            WHERE upload_status IN ('pending', 'failed')
            """;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<MovieScoreCredit> GetPendingCreditsForUser(string userId, DateTime now)
    {
        var list = new List<MovieScoreCredit>();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return list;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, gift_event_id, user_id, nickname, gift_name, diamond_count, value,
                   points, created_at, expires_at, consumed_at, score_event_id, status
            FROM movie_score_credits
            WHERE user_id = $uid AND status = 'pending' AND expires_at > $now
            ORDER BY created_at ASC, id ASC
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadCredit(reader));
        }

        return list;
    }

    /// <summary>
    /// 判断用户近期是否有刚过期的 credit，用于区分「从未有机会」与「刚过期」。
    /// </summary>
    public bool HasRecentlyExpiredCredit(string userId, DateTime now, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        var since = now - window;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM movie_score_credits
            WHERE user_id = $uid
              AND status = 'expired'
              AND expires_at >= $since
              AND expires_at <= $now
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// 事务：消费用户全部未过期 pending 积分 + 写入评分事件 + 更新总分 + 写入事件流。
    /// 条件更新保证同一积分不会被并发二次消费。
    /// </summary>
    public bool TryApplyScore(
        MovieScoreEvent scoreEvent,
        IReadOnlyList<long> creditIds,
        Func<long, object> streamPayloadFactory,
        out long totalAfter,
        out string failureReason)
    {
        totalAfter = 0;
        failureReason = "";
        if (creditIds.Count == 0 || string.IsNullOrWhiteSpace(scoreEvent.EventId))
        {
            failureReason = "empty";
            return false;
        }

        var sourceJson = JsonSerializer.Serialize(scoreEvent.SourceGiftEventIds, JsonOptions);
        var now = scoreEvent.CreatedAt.ToString("O");

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            // 幂等：事件已存在则拒绝（不重复消费）
            using (var existsCmd = conn.CreateCommand())
            {
                existsCmd.Transaction = tx;
                existsCmd.CommandText = "SELECT COUNT(1) FROM movie_score_events WHERE event_id = $eid";
                existsCmd.Parameters.AddWithValue("$eid", scoreEvent.EventId);
                if (Convert.ToInt32(existsCmd.ExecuteScalar()) > 0)
                {
                    failureReason = "duplicate_event";
                    tx.Rollback();
                    return false;
                }
            }

            var idList = string.Join(",", creditIds.Select(id => id.ToString()));
            using (var consumeCmd = conn.CreateCommand())
            {
                consumeCmd.Transaction = tx;
                consumeCmd.CommandText = $"""
                    UPDATE movie_score_credits
                    SET status = 'consumed',
                        consumed_at = $now,
                        score_event_id = $seid
                    WHERE id IN ({idList})
                      AND user_id = $uid
                      AND status = 'pending'
                      AND expires_at > $expNow
                    """;
                consumeCmd.Parameters.AddWithValue("$now", now);
                consumeCmd.Parameters.AddWithValue("$seid", scoreEvent.EventId);
                consumeCmd.Parameters.AddWithValue("$uid", scoreEvent.UserId);
                consumeCmd.Parameters.AddWithValue("$expNow", now);
                var consumed = consumeCmd.ExecuteNonQuery();
                if (consumed != creditIds.Count)
                {
                    failureReason = $"consume_mismatch expected={creditIds.Count} actual={consumed}";
                    tx.Rollback();
                    return false;
                }
            }

            using (var insertEvent = conn.CreateCommand())
            {
                insertEvent.Transaction = tx;
                insertEvent.CommandText = """
                    INSERT INTO movie_score_events (
                        event_id, platform, room_id, user_id, nickname,
                        movie_id, movie_name, action, score_delta, absolute_points,
                        source_gift_event_ids, created_at, upload_status, upload_attempts)
                    VALUES (
                        $eid, $platform, $room, $uid, $nick,
                        $mid, $mname, $action, $delta, $abs,
                        $sources, $created, 'pending', 0)
                    """;
                insertEvent.Parameters.AddWithValue("$eid", scoreEvent.EventId);
                insertEvent.Parameters.AddWithValue("$platform", scoreEvent.Platform ?? "douyin");
                insertEvent.Parameters.AddWithValue("$room", scoreEvent.RoomId ?? "");
                insertEvent.Parameters.AddWithValue("$uid", scoreEvent.UserId);
                insertEvent.Parameters.AddWithValue("$nick", scoreEvent.Nickname ?? "");
                insertEvent.Parameters.AddWithValue("$mid", scoreEvent.MovieId);
                insertEvent.Parameters.AddWithValue("$mname", scoreEvent.MovieName);
                insertEvent.Parameters.AddWithValue("$action", scoreEvent.Action);
                insertEvent.Parameters.AddWithValue("$delta", scoreEvent.ScoreDelta);
                insertEvent.Parameters.AddWithValue("$abs", scoreEvent.AbsolutePoints);
                insertEvent.Parameters.AddWithValue("$sources", sourceJson);
                insertEvent.Parameters.AddWithValue("$created", now);
                insertEvent.ExecuteNonQuery();
            }

            using (var upsertTotal = conn.CreateCommand())
            {
                upsertTotal.Transaction = tx;
                upsertTotal.CommandText = """
                    INSERT INTO movie_score_totals (movie_id, movie_name, score, updated_at)
                    VALUES ($mid, $mname, $delta, $now)
                    ON CONFLICT(movie_id) DO UPDATE SET
                        score = score + excluded.score,
                        movie_name = excluded.movie_name,
                        updated_at = excluded.updated_at
                    """;
                upsertTotal.Parameters.AddWithValue("$mid", scoreEvent.MovieId);
                upsertTotal.Parameters.AddWithValue("$mname", scoreEvent.MovieName);
                upsertTotal.Parameters.AddWithValue("$delta", scoreEvent.ScoreDelta);
                upsertTotal.Parameters.AddWithValue("$now", now);
                upsertTotal.ExecuteNonQuery();
            }

            totalAfter = GetTotalScore(conn, tx, scoreEvent.MovieId);
            var streamJson = JsonSerializer.Serialize(streamPayloadFactory(totalAfter), JsonOptions);

            using (var streamCmd = conn.CreateCommand())
            {
                streamCmd.Transaction = tx;
                streamCmd.CommandText = """
                    INSERT INTO movie_interaction_stream (type, ref_id, payload_json, created_at)
                    VALUES ($type, $ref, $payload, $created)
                    """;
                streamCmd.Parameters.AddWithValue("$type", "movie_score");
                streamCmd.Parameters.AddWithValue("$ref", scoreEvent.EventId);
                streamCmd.Parameters.AddWithValue("$payload", streamJson);
                streamCmd.Parameters.AddWithValue("$created", now);
                streamCmd.ExecuteNonQuery();
            }

            tx.Commit();
            return true;
        }
        catch (Exception)
        {
            try { tx.Rollback(); } catch { /* ignore */ }
            throw;
        }
    }

    public void AppendDanmakuStream(string msgId, object payload, DateTime createdAt)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO movie_interaction_stream (type, ref_id, payload_json, created_at)
            VALUES ('danmaku', $ref, $payload, $created)
            """;
        cmd.Parameters.AddWithValue("$ref", msgId ?? "");
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload, JsonOptions));
        cmd.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<MovieInteractionStreamItem> GetStreamAfter(long afterSeq, int limit = 200)
    {
        var list = new List<MovieInteractionStreamItem>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT seq, type, payload_json, created_at
            FROM movie_interaction_stream
            WHERE seq > $after
            ORDER BY seq ASC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$after", afterSeq);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MovieInteractionStreamItem
            {
                Seq = reader.GetInt64(0),
                Type = reader.IsDBNull(1) ? "" : reader.GetString(1),
                PayloadJson = reader.IsDBNull(2) ? "{}" : reader.GetString(2),
                CreatedAt = DateTime.TryParse(reader.GetString(3), out var dt) ? dt : DateTime.Now
            });
        }

        return list;
    }

    public long GetStreamMaxSeq() => _db.GetStreamMaxSeq();

    public string GetStreamEpoch() => _db.GetOrCreateStreamEpoch();

    /// <summary>
    /// 清理足够旧的 stream 行；不碰评分账本。仅删除 seq 远小于当前 max 的旧行，避免破坏 cursor。
    /// </summary>
    public int CleanupOldStream(DateTime cutoff, long keepRecentSeqCount = 1000)
    {
        using var conn = _db.Open();
        long maxSeq;
        using (var maxCmd = conn.CreateCommand())
        {
            maxCmd.CommandText = "SELECT IFNULL(MAX(seq), 0) FROM movie_interaction_stream";
            maxSeq = Convert.ToInt64(maxCmd.ExecuteScalar() ?? 0L);
        }

        var protectBelow = Math.Max(0, maxSeq - Math.Max(0, keepRecentSeqCount));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM movie_interaction_stream
            WHERE created_at < $cutoff AND seq < $protect
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        cmd.Parameters.AddWithValue("$protect", protectBelow);
        return cmd.ExecuteNonQuery();
    }

    public void ReplaceCatalog(IReadOnlyList<MovieCatalogEntry> movies)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM movie_catalog";
                clear.ExecuteNonQuery();
            }

            foreach (var m in movies)
            {
                if (string.IsNullOrWhiteSpace(m.MovieId) || string.IsNullOrWhiteSpace(m.MovieName))
                {
                    continue;
                }

                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO movie_catalog (movie_id, movie_name, aliases_json, rank, updated_at)
                    VALUES ($id, $name, $aliases, $rank, $updated)
                    """;
                insert.Parameters.AddWithValue("$id", m.MovieId.Trim());
                insert.Parameters.AddWithValue("$name", m.MovieName.Trim());
                insert.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(m.Aliases ?? new List<string>(), JsonOptions));
                insert.Parameters.AddWithValue("$rank", m.Rank);
                insert.Parameters.AddWithValue("$updated", m.UpdatedAt.ToString("O"));
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { /* ignore */ }
            throw;
        }
    }

    public List<MovieCatalogEntry> GetCatalog()
    {
        var list = new List<MovieCatalogEntry>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT movie_id, movie_name, aliases_json, rank, updated_at
            FROM movie_catalog
            ORDER BY rank ASC, movie_id ASC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var aliasesJson = reader.IsDBNull(2) ? "[]" : reader.GetString(2);
            List<string> aliases;
            try
            {
                aliases = JsonSerializer.Deserialize<List<string>>(aliasesJson, JsonOptions) ?? new List<string>();
            }
            catch
            {
                aliases = new List<string>();
            }

            list.Add(new MovieCatalogEntry
            {
                MovieId = reader.GetString(0),
                MovieName = reader.GetString(1),
                Aliases = aliases,
                Rank = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                UpdatedAt = DateTime.TryParse(reader.GetString(4), out var dt) ? dt : DateTime.Now
            });
        }

        return list;
    }

    public int CountCatalog()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM movie_catalog";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<MovieScoreTotal> GetTotals()
    {
        var list = new List<MovieScoreTotal>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.movie_id,
                   COALESCE(NULLIF(c.movie_name, ''), t.movie_name) AS movie_name,
                   t.score,
                   t.updated_at,
                   COALESCE((
                       SELECT COUNT(1)
                       FROM movie_score_events e
                       WHERE e.movie_id = t.movie_id AND e.action = 'good'
                   ), 0) AS good_user_count,
                   COALESCE((
                       SELECT COUNT(1)
                       FROM movie_score_events e
                       WHERE e.movie_id = t.movie_id AND e.action = 'bad'
                   ), 0) AS bad_user_count
            FROM movie_score_totals t
            LEFT JOIN movie_catalog c ON c.movie_id = t.movie_id
            ORDER BY t.score DESC, t.movie_id ASC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MovieScoreTotal
            {
                MovieId = reader.GetString(0),
                MovieName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Score = reader.GetInt64(2),
                UpdatedAt = DateTime.TryParse(reader.GetString(3), out var dt) ? dt : DateTime.Now,
                GoodUserCount = reader.GetInt64(4),
                BadUserCount = reader.GetInt64(5)
            });
        }

        return list;
    }

    public long GetTotalScore(string movieId)
    {
        using var conn = _db.Open();
        return GetTotalScore(conn, null, movieId);
    }

    public List<MovieScoreEvent> GetPendingSyncEvents(DateTime now, int limit = 20)
    {
        var list = new List<MovieScoreEvent>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, platform, room_id, user_id, nickname, movie_id, movie_name,
                   action, score_delta, absolute_points, source_gift_event_ids,
                   created_at, uploaded_at, upload_status, upload_attempts, next_retry_at
            FROM movie_score_events
            WHERE upload_status IN ('pending', 'failed')
              AND (next_retry_at IS NULL OR next_retry_at <= $now)
            ORDER BY created_at ASC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadScoreEvent(reader));
        }

        return list;
    }

    public void MarkUploadSuccess(string eventId, DateTime uploadedAt)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE movie_score_events
            SET upload_status = 'synced', uploaded_at = $at, next_retry_at = NULL
            WHERE event_id = $eid
            """;
        cmd.Parameters.AddWithValue("$at", uploadedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$eid", eventId);
        cmd.ExecuteNonQuery();
    }

    public void MarkUploadFailure(string eventId, int attempts, DateTime nextRetryAt)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE movie_score_events
            SET upload_status = 'failed',
                upload_attempts = $attempts,
                next_retry_at = $next
            WHERE event_id = $eid
            """;
        cmd.Parameters.AddWithValue("$attempts", attempts);
        cmd.Parameters.AddWithValue("$next", nextRetryAt.ToString("O"));
        cmd.Parameters.AddWithValue("$eid", eventId);
        cmd.ExecuteNonQuery();
    }

    public MovieScoreCredit? GetCreditByGiftEventId(string giftEventId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, gift_event_id, user_id, nickname, gift_name, diamond_count, value,
                   points, created_at, expires_at, consumed_at, score_event_id, status
            FROM movie_score_credits WHERE gift_event_id = $eid LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$eid", giftEventId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCredit(reader) : null;
    }

    public List<MovieScoreCredit> GetCreditsByUser(string userId)
    {
        var list = new List<MovieScoreCredit>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, gift_event_id, user_id, nickname, gift_name, diamond_count, value,
                   points, created_at, expires_at, consumed_at, score_event_id, status
            FROM movie_score_credits WHERE user_id = $uid ORDER BY id ASC
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadCredit(reader));
        }

        return list;
    }

    public MovieScoreEvent? GetScoreEvent(string eventId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, platform, room_id, user_id, nickname, movie_id, movie_name,
                   action, score_delta, absolute_points, source_gift_event_ids,
                   created_at, uploaded_at, upload_status, upload_attempts, next_retry_at
            FROM movie_score_events WHERE event_id = $eid LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$eid", eventId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadScoreEvent(reader) : null;
    }

    private static long GetTotalScore(SqliteConnection conn, SqliteTransaction? tx, string movieId)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null)
        {
            cmd.Transaction = tx;
        }

        cmd.CommandText = "SELECT score FROM movie_score_totals WHERE movie_id = $mid";
        cmd.Parameters.AddWithValue("$mid", movieId);
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0L : Convert.ToInt64(result);
    }

    private static MovieScoreCredit ReadCredit(SqliteDataReader reader)
    {
        return new MovieScoreCredit
        {
            Id = reader.GetInt64(0),
            GiftEventId = reader.GetString(1),
            UserId = reader.GetString(2),
            Nickname = reader.IsDBNull(3) ? "" : reader.GetString(3),
            GiftName = reader.IsDBNull(4) ? "" : reader.GetString(4),
            DiamondCount = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            Value = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            Points = reader.GetInt32(7),
            CreatedAt = DateTime.TryParse(reader.GetString(8), out var c) ? c : DateTime.Now,
            ExpiresAt = DateTime.TryParse(reader.GetString(9), out var e) ? e : DateTime.Now,
            ConsumedAt = reader.IsDBNull(10) ? null : (DateTime.TryParse(reader.GetString(10), out var cons) ? cons : null),
            ScoreEventId = reader.IsDBNull(11) ? null : reader.GetString(11),
            Status = reader.GetString(12)
        };
    }

    private static MovieScoreEvent ReadScoreEvent(SqliteDataReader reader)
    {
        var sourcesJson = reader.IsDBNull(10) ? "[]" : reader.GetString(10);
        List<string> sources;
        try
        {
            sources = JsonSerializer.Deserialize<List<string>>(sourcesJson, JsonOptions) ?? new List<string>();
        }
        catch
        {
            sources = new List<string>();
        }

        return new MovieScoreEvent
        {
            EventId = reader.GetString(0),
            Platform = reader.IsDBNull(1) ? "douyin" : reader.GetString(1),
            RoomId = reader.IsDBNull(2) ? "" : reader.GetString(2),
            UserId = reader.GetString(3),
            Nickname = reader.IsDBNull(4) ? "" : reader.GetString(4),
            MovieId = reader.GetString(5),
            MovieName = reader.GetString(6),
            Action = reader.GetString(7),
            ScoreDelta = reader.GetInt32(8),
            AbsolutePoints = reader.GetInt32(9),
            SourceGiftEventIds = sources,
            CreatedAt = DateTime.TryParse(reader.GetString(11), out var c) ? c : DateTime.Now,
            UploadedAt = reader.IsDBNull(12) ? null : (DateTime.TryParse(reader.GetString(12), out var u) ? u : null),
            UploadStatus = reader.IsDBNull(13) ? "pending" : reader.GetString(13),
            UploadAttempts = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
            NextRetryAt = reader.IsDBNull(15) ? null : (DateTime.TryParse(reader.GetString(15), out var n) ? n : null)
        };
    }
}
