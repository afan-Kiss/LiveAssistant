using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

/// <summary>
/// 点歌扣费生命周期：charged → fulfilled / refunded，退款幂等。
/// </summary>
public sealed class SongRequestChargeRepository
{
    private readonly AppDatabase _db;
    private readonly PointsLedgerRepository _ledger;

    public SongRequestChargeRepository(AppDatabase db, PointsLedgerRepository? ledger = null)
    {
        _db = db;
        _ledger = ledger ?? new PointsLedgerRepository(db);
    }

    public bool TryInsertCharge(
        long queueItemId,
        string userId,
        string nickname,
        string chargeType,
        int pointsDeducted,
        int creditConsumed,
        out string? failureReason)
    {
        failureReason = null;
        if (queueItemId <= 0 || string.IsNullOrWhiteSpace(userId))
        {
            failureReason = "invalid_args";
            return false;
        }

        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO song_request_charges (
                queue_item_id, user_id, nickname, charge_type,
                points_deducted, credit_consumed, status, created_at)
            VALUES (
                $qid, $uid, $nick, $ctype,
                $pts, $credit, 'charged', $now)
            """;
        cmd.Parameters.AddWithValue("$qid", queueItemId);
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$nick", nickname ?? "");
        cmd.Parameters.AddWithValue("$ctype", chargeType);
        cmd.Parameters.AddWithValue("$pts", Math.Max(0, pointsDeducted));
        cmd.Parameters.AddWithValue("$credit", Math.Max(0, creditConsumed));
        cmd.Parameters.AddWithValue("$now", now);
        try
        {
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            failureReason = "duplicate_charge";
            return false;
        }
    }

    public SongRequestCharge? GetByQueueItemId(long queueItemId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, queue_item_id, user_id, nickname, charge_type,
                   points_deducted, credit_consumed, status, refund_reason,
                   created_at, fulfilled_at, refunded_at
            FROM song_request_charges
            WHERE queue_item_id = $qid
            """;
        cmd.Parameters.AddWithValue("$qid", queueItemId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCharge(reader) : null;
    }

    public bool TryMarkFulfilled(long queueItemId)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE song_request_charges
            SET status = 'fulfilled', fulfilled_at = $now
            WHERE queue_item_id = $qid AND status = 'charged'
            """;
        cmd.Parameters.AddWithValue("$qid", queueItemId);
        cmd.Parameters.AddWithValue("$now", now);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 幂等退款：仅 charged 可退；同一 queueItemId 第二次返回 already_refunded。
    /// 积分退回与状态更新在同一事务。
    /// </summary>
    public SongRequestRefundResult RefundSongRequestCharge(long queueItemId, string reason)
    {
        if (queueItemId <= 0)
        {
            return SongRequestRefundResult.Fail("invalid_queue_item");
        }

        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            SongRequestCharge? charge;
            using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT id, queue_item_id, user_id, nickname, charge_type,
                           points_deducted, credit_consumed, status, refund_reason,
                           created_at, fulfilled_at, refunded_at
                    FROM song_request_charges
                    WHERE queue_item_id = $qid
                    """;
                read.Parameters.AddWithValue("$qid", queueItemId);
                using var reader = read.ExecuteReader();
                if (!reader.Read())
                {
                    tx.Commit();
                    return SongRequestRefundResult.NotRefundable("no_charge");
                }

                charge = ReadCharge(reader);
            }

            if (string.Equals(charge.Status, SongRequestChargeStatus.Refunded, StringComparison.OrdinalIgnoreCase))
            {
                tx.Commit();
                return SongRequestRefundResult.AlreadyRefunded();
            }

            if (string.Equals(charge.Status, SongRequestChargeStatus.Fulfilled, StringComparison.OrdinalIgnoreCase))
            {
                tx.Commit();
                return SongRequestRefundResult.NotRefundable("already_fulfilled");
            }

            if (!string.Equals(charge.Status, SongRequestChargeStatus.Charged, StringComparison.OrdinalIgnoreCase))
            {
                tx.Commit();
                return SongRequestRefundResult.NotRefundable("not_charged");
            }

            var pointsRestored = 0;
            var creditRestored = 0;

            if (charge.PointsDeducted > 0)
            {
                using (var ensure = conn.CreateCommand())
                {
                    ensure.Transaction = tx;
                    ensure.CommandText = """
                        INSERT OR IGNORE INTO users (user_id, nickname, role, status, points, level, request_count, created_at, updated_at)
                        VALUES ($uid, $nick, 'normal', 'active', 0, 0, 0, $now, $now)
                        """;
                    ensure.Parameters.AddWithValue("$uid", charge.UserId);
                    ensure.Parameters.AddWithValue("$nick", charge.Nickname ?? "");
                    ensure.Parameters.AddWithValue("$now", now);
                    ensure.ExecuteNonQuery();
                }

                using (var upd = conn.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = """
                        UPDATE users
                        SET points = points + $pts, updated_at = $now
                        WHERE user_id = $uid
                        """;
                    upd.Parameters.AddWithValue("$uid", charge.UserId);
                    upd.Parameters.AddWithValue("$pts", charge.PointsDeducted);
                    upd.Parameters.AddWithValue("$now", now);
                    if (upd.ExecuteNonQuery() == 0)
                    {
                        tx.Rollback();
                        return SongRequestRefundResult.Fail("points_restore_failed");
                    }
                }

                int balanceAfter;
                using (var bal = conn.CreateCommand())
                {
                    bal.Transaction = tx;
                    bal.CommandText = "SELECT points FROM users WHERE user_id = $uid";
                    bal.Parameters.AddWithValue("$uid", charge.UserId);
                    balanceAfter = Convert.ToInt32(bal.ExecuteScalar() ?? 0);
                }

                _ledger.Insert(
                    conn,
                    tx,
                    charge.UserId,
                    charge.PointsDeducted,
                    balanceAfter,
                    PointsTransactionType.Refund,
                    $"点歌未播放退款:{reason}",
                    queueItemId.ToString());
                pointsRestored = charge.PointsDeducted;
            }

            if (charge.CreditConsumed > 0)
            {
                using (var ensure = conn.CreateCommand())
                {
                    ensure.Transaction = tx;
                    ensure.CommandText = """
                        INSERT OR IGNORE INTO users (user_id, nickname, role, status, points, level, request_count, created_at, updated_at)
                        VALUES ($uid, $nick, 'normal', 'active', 0, 0, 0, $now, $now)
                        """;
                    ensure.Parameters.AddWithValue("$uid", charge.UserId);
                    ensure.Parameters.AddWithValue("$nick", charge.Nickname ?? "");
                    ensure.Parameters.AddWithValue("$now", now);
                    ensure.ExecuteNonQuery();
                }

                using (var upd = conn.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = """
                        UPDATE users
                        SET song_permission_credits = song_permission_credits + $n, updated_at = $now
                        WHERE user_id = $uid
                        """;
                    upd.Parameters.AddWithValue("$uid", charge.UserId);
                    upd.Parameters.AddWithValue("$n", charge.CreditConsumed);
                    upd.Parameters.AddWithValue("$now", now);
                    if (upd.ExecuteNonQuery() == 0)
                    {
                        tx.Rollback();
                        return SongRequestRefundResult.Fail("credit_restore_failed");
                    }
                }

                creditRestored = charge.CreditConsumed;
            }

            using (var mark = conn.CreateCommand())
            {
                mark.Transaction = tx;
                mark.CommandText = """
                    UPDATE song_request_charges
                    SET status = 'refunded', refund_reason = $reason, refunded_at = $now
                    WHERE queue_item_id = $qid AND status = 'charged'
                    """;
                mark.Parameters.AddWithValue("$qid", queueItemId);
                mark.Parameters.AddWithValue("$reason", reason ?? "");
                mark.Parameters.AddWithValue("$now", now);
                if (mark.ExecuteNonQuery() == 0)
                {
                    tx.Rollback();
                    return SongRequestRefundResult.AlreadyRefunded();
                }
            }

            tx.Commit();
            return SongRequestRefundResult.Ok(pointsRestored, creditRestored);
        }
        catch
        {
            try { tx.Rollback(); } catch { /* ignore */ }
            throw;
        }
    }

    public List<long> ListChargedQueueItemIds(IEnumerable<long> queueItemIds)
    {
        var ids = queueItemIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new List<long>();
        }

        var result = new List<long>();
        using var conn = _db.Open();
        foreach (var id in ids)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT queue_item_id FROM song_request_charges
                WHERE queue_item_id = $qid AND status = 'charged'
                """;
            cmd.Parameters.AddWithValue("$qid", id);
            var found = cmd.ExecuteScalar();
            if (found != null && found != DBNull.Value)
            {
                result.Add(Convert.ToInt64(found));
            }
        }

        return result;
    }

    public List<long> ListChargedPendingQueueItemIds()
    {
        var list = new List<long>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.queue_item_id
            FROM song_request_charges c
            INNER JOIN queue_items q ON q.id = c.queue_item_id
            WHERE c.status = 'charged'
              AND q.status IN ('waiting', 'playing')
              AND IFNULL(q.is_random, 0) = 0
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }

    private static SongRequestCharge ReadCharge(SqliteDataReader reader)
    {
        return new SongRequestCharge
        {
            Id = reader.GetInt64(0),
            QueueItemId = reader.GetInt64(1),
            UserId = reader.IsDBNull(2) ? "" : reader.GetString(2),
            Nickname = reader.IsDBNull(3) ? "" : reader.GetString(3),
            ChargeType = reader.IsDBNull(4) ? SongRequestChargeType.Free : reader.GetString(4),
            PointsDeducted = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            CreditConsumed = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            Status = reader.IsDBNull(7) ? SongRequestChargeStatus.Charged : reader.GetString(7),
            RefundReason = reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedAt = DateTime.TryParse(reader.IsDBNull(9) ? null : reader.GetString(9), out var c) ? c : DateTime.Now,
            FulfilledAt = DateTime.TryParse(reader.IsDBNull(10) ? null : reader.GetString(10), out var f) ? f : null,
            RefundedAt = DateTime.TryParse(reader.IsDBNull(11) ? null : reader.GetString(11), out var r) ? r : null
        };
    }
}
