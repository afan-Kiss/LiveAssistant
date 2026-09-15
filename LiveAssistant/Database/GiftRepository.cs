using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class GiftRepository
{
    private readonly AppDatabase _db;
    private readonly PointsLedgerRepository _ledger;

    public GiftRepository(AppDatabase db, PointsLedgerRepository? ledger = null)
    {
        _db = db;
        _ledger = ledger ?? new PointsLedgerRepository(db);
    }

    public bool ExistsByEventId(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM gift_events WHERE event_id = $eid";
        cmd.Parameters.AddWithValue("$eid", eventId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public bool TryInsert(GiftEvent gift, int pointsDelta, int pointsAfter, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(gift.EventId))
        {
            return false;
        }

        if (ExistsByEventId(gift.EventId))
        {
            return false;
        }

        var now = gift.Time.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gift_events (event_id, user_id, nickname, gift_id, gift_name, count, value, points_delta, points_after, created_at)
            VALUES ($eid, $uid, $nick, $gid, $gname, $cnt, $val, $pd, $pa, $now)
            """;
        cmd.Parameters.AddWithValue("$eid", gift.EventId);
        cmd.Parameters.AddWithValue("$uid", gift.UserId);
        cmd.Parameters.AddWithValue("$nick", gift.Nickname);
        cmd.Parameters.AddWithValue("$gid", gift.GiftId);
        cmd.Parameters.AddWithValue("$gname", gift.GiftName);
        cmd.Parameters.AddWithValue("$cnt", gift.Count);
        cmd.Parameters.AddWithValue("$val", gift.Value);
        cmd.Parameters.AddWithValue("$pd", pointsDelta);
        cmd.Parameters.AddWithValue("$pa", pointsAfter);
        cmd.Parameters.AddWithValue("$now", now);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            return false;
        }

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        id = (long)(idCmd.ExecuteScalar() ?? 0L);
        return true;
    }

    /// <summary>
    /// 原子写入礼物记录并更新用户积分/等级/点歌权限；失败自动回滚。
    /// </summary>
    public bool TryRecordGift(
        GiftEvent gift,
        int pointsDelta,
        int pointsAfter,
        int newLevel,
        bool applyPointsAndLevel,
        bool setSongPermissionUnlimited,
        int songPermissionCreditsDelta,
        out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(gift.EventId) || string.IsNullOrWhiteSpace(gift.UserId))
        {
            return false;
        }

        var giftTime = gift.Time.ToString("O");
        var now = DateTime.Now.ToString("O");

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            using (var ensureCmd = conn.CreateCommand())
            {
                ensureCmd.Transaction = tx;
                ensureCmd.CommandText = """
                    INSERT OR IGNORE INTO users (user_id, nickname, role, status, points, level, request_count, created_at, updated_at)
                    VALUES ($uid, $nick, 'normal', 'active', 0, 0, 0, $now, $now)
                    """;
                ensureCmd.Parameters.AddWithValue("$uid", gift.UserId);
                ensureCmd.Parameters.AddWithValue("$nick", gift.Nickname);
                ensureCmd.Parameters.AddWithValue("$now", now);
                ensureCmd.ExecuteNonQuery();
            }

            using (var insertCmd = conn.CreateCommand())
            {
                insertCmd.Transaction = tx;
                insertCmd.CommandText = """
                    INSERT INTO gift_events (event_id, user_id, nickname, gift_id, gift_name, count, value, points_delta, points_after, created_at)
                    VALUES ($eid, $uid, $nick, $gid, $gname, $cnt, $val, $pd, $pa, $giftTime)
                    """;
                insertCmd.Parameters.AddWithValue("$eid", gift.EventId);
                insertCmd.Parameters.AddWithValue("$uid", gift.UserId);
                insertCmd.Parameters.AddWithValue("$nick", gift.Nickname);
                insertCmd.Parameters.AddWithValue("$gid", gift.GiftId);
                insertCmd.Parameters.AddWithValue("$gname", gift.GiftName);
                insertCmd.Parameters.AddWithValue("$cnt", gift.Count);
                insertCmd.Parameters.AddWithValue("$val", gift.Value);
                insertCmd.Parameters.AddWithValue("$pd", pointsDelta);
                insertCmd.Parameters.AddWithValue("$pa", pointsAfter);
                insertCmd.Parameters.AddWithValue("$giftTime", giftTime);
                insertCmd.ExecuteNonQuery();
            }

            using (var idCmd = conn.CreateCommand())
            {
                idCmd.Transaction = tx;
                idCmd.CommandText = "SELECT last_insert_rowid()";
                id = (long)(idCmd.ExecuteScalar() ?? 0L);
            }

            using (var userCmd = conn.CreateCommand())
            {
                userCmd.Transaction = tx;
                userCmd.CommandText = """
                    UPDATE users SET
                        nickname = CASE WHEN length($nick) > 0 THEN $nick ELSE nickname END,
                        points = points + $pts,
                        level = CASE WHEN $applyLevel = 1 THEN $level ELSE level END,
                        song_permission_unlimited = CASE WHEN $unlimited = 1 THEN 1 ELSE song_permission_unlimited END,
                        song_permission_credits = song_permission_credits + $permCredits,
                        updated_at = $now
                    WHERE user_id = $uid
                    """;
                userCmd.Parameters.AddWithValue("$uid", gift.UserId);
                userCmd.Parameters.AddWithValue("$nick", gift.Nickname);
                userCmd.Parameters.AddWithValue("$pts", applyPointsAndLevel ? pointsDelta : 0);
                userCmd.Parameters.AddWithValue("$applyLevel", applyPointsAndLevel ? 1 : 0);
                userCmd.Parameters.AddWithValue("$level", newLevel);
                userCmd.Parameters.AddWithValue("$unlimited", setSongPermissionUnlimited ? 1 : 0);
                userCmd.Parameters.AddWithValue("$permCredits", Math.Max(0, songPermissionCreditsDelta));
                userCmd.Parameters.AddWithValue("$now", now);
                userCmd.ExecuteNonQuery();
            }

            if (applyPointsAndLevel && pointsDelta > 0)
            {
                _ledger.Insert(
                    conn,
                    tx,
                    gift.UserId,
                    pointsDelta,
                    pointsAfter,
                    PointsTransactionType.Gift,
                    $"礼物 {gift.GiftName}×{gift.Count}",
                    gift.EventId,
                    gift.Time);
            }

            tx.Commit();
            return true;
        }
        catch (SqliteException)
        {
            try
            {
                tx.Rollback();
            }
            catch
            {
                // ignore rollback errors
            }

            return false;
        }
        catch
        {
            try
            {
                tx.Rollback();
            }
            catch
            {
                // ignore rollback errors
            }

            throw;
        }
    }

    public List<GiftEvent> ListRecent(int limit = 50)
    {
        var list = new List<GiftEvent>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_id, user_id, nickname, gift_id, gift_name, count, value, created_at
            FROM gift_events ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GiftEvent
            {
                Id = reader.GetInt64(0),
                EventId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                UserId = reader.GetString(2),
                Nickname = reader.GetString(3),
                GiftId = reader.GetString(4),
                GiftName = reader.GetString(5),
                Count = reader.GetInt32(6),
                Value = reader.GetInt32(7),
                Time = DateTime.TryParse(reader.GetString(8), out var dt) ? dt : DateTime.Now,
                CreatedAt = DateTime.TryParse(reader.GetString(8), out var c) ? c : DateTime.Now
            });
        }
        return list;
    }

    public int SumGiftPointsForUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return 0;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(points_delta), 0) FROM gift_events WHERE user_id = $uid
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountTodayGifts()
    {
        var today = DateTime.Today.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM gift_events WHERE created_at >= $today";
        cmd.Parameters.AddWithValue("$today", today);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool HasReceivedGift(string userId, string giftName)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(giftName))
        {
            return false;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM gift_events
            WHERE user_id = $uid AND gift_name = $name COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$name", giftName.Trim());
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }
}
