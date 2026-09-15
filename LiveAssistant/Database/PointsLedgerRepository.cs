using LiveAssistant.Models;
using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class PointsLedgerRepository
{
    private readonly AppDatabase _db;

    public PointsLedgerRepository(AppDatabase db) => _db = db;

    public void Insert(
        SqliteConnection conn,
        SqliteTransaction tx,
        string userId,
        int delta,
        int balanceAfter,
        string type,
        string reason,
        string? refId,
        DateTime? createdAt = null,
        string? operatorName = null)
    {
        var now = (createdAt ?? DateTime.Now).ToString("O");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO points_ledger (user_id, delta, balance_after, type, reason, ref_id, operator_name, created_at)
            VALUES ($uid, $delta, $after, $type, $reason, $refId, $op, $now)
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$delta", delta);
        cmd.Parameters.AddWithValue("$after", balanceAfter);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$refId", refId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$op", string.IsNullOrWhiteSpace(operatorName) ? DBNull.Value : operatorName);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public List<PointsLedgerEntry> ListByUser(string userId, int limit = 20, int offset = 0)
    {
        var list = new List<PointsLedgerEntry>();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return list;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, delta, balance_after, type, reason, ref_id, operator_name, created_at
            FROM points_ledger
            WHERE user_id = $uid
            ORDER BY id DESC
            LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadEntry(reader));
        }

        return list;
    }

    public int CountByUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return 0;
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM points_ledger WHERE user_id = $uid";
        cmd.Parameters.AddWithValue("$uid", userId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public string FormatTypeLabel(string type) => type switch
    {
        PointsTransactionType.Gift => "礼物奖励",
        PointsTransactionType.SongRequest => "点歌消费",
        PointsTransactionType.SkipSong => "切歌消费",
        PointsTransactionType.AdminSet => "管理员设置",
        PointsTransactionType.AdminAdjust => "管理员调整",
        PointsTransactionType.Refund => "退款",
        _ => type
    };

    private static PointsLedgerEntry ReadEntry(SqliteDataReader reader)
    {
        var hasOperator = reader.FieldCount > 8;
        var createdIdx = hasOperator ? 8 : 7;
        DateTime createdAt = DateTime.Now;
        if (!reader.IsDBNull(createdIdx) && DateTime.TryParse(reader.GetString(createdIdx), out var dt))
        {
            createdAt = dt;
        }

        return new PointsLedgerEntry
        {
            Id = reader.GetInt64(0),
            UserId = reader.GetString(1),
            Delta = reader.GetInt32(2),
            BalanceAfter = reader.GetInt32(3),
            Type = reader.IsDBNull(4) ? "" : reader.GetString(4),
            Reason = reader.IsDBNull(5) ? "" : reader.GetString(5),
            RefId = reader.IsDBNull(6) ? null : reader.GetString(6),
            OperatorName = hasOperator && !reader.IsDBNull(7) ? reader.GetString(7) : null,
            CreatedAt = createdAt
        };
    }
}
