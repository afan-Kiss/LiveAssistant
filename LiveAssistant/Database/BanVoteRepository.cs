using Microsoft.Data.Sqlite;

namespace LiveAssistant.Database;

public sealed class BanVoteSession
{
    public string Id { get; set; } = "";
    public string TargetUserId { get; set; } = "";
    public string TargetNickname { get; set; } = "";
    public int VoteCount { get; set; }
    public int RequiredVotes { get; set; }
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; }
}

public sealed class BanVoteRepository
{
    private readonly AppDatabase _db;

    public BanVoteRepository(AppDatabase db)
    {
        _db = db;
    }

    public BanVoteSession? GetActiveSession(string targetUserId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, target_user_id, target_nickname, vote_count, required_votes, status, created_at
            FROM ban_vote_sessions
            WHERE target_user_id = $uid AND status = 'active'
            ORDER BY created_at DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$uid", targetUserId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ReadSession(reader);
    }

    public BanVoteSession CreateSession(string targetUserId, string targetNickname, int requiredVotes)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ban_vote_sessions (id, target_user_id, target_nickname, vote_count, required_votes, status, created_at)
            VALUES ($id, $uid, $nick, 0, $req, 'active', $now)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$uid", targetUserId);
        cmd.Parameters.AddWithValue("$nick", targetNickname);
        cmd.Parameters.AddWithValue("$req", requiredVotes);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return new BanVoteSession
        {
            Id = id,
            TargetUserId = targetUserId,
            TargetNickname = targetNickname,
            RequiredVotes = requiredVotes,
            CreatedAt = DateTime.Now
        };
    }

    public bool HasVoted(string sessionId, string voterUserId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM ban_votes WHERE session_id = $sid AND voter_user_id = $vid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$vid", voterUserId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public int AddVote(string sessionId, string targetUserId, string targetNickname, string voterUserId, string voterNickname)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ban_votes (session_id, target_user_id, target_nickname, voter_user_id, voter_nickname, created_at)
            VALUES ($sid, $tuid, $tnick, $vuid, $vnick, $now)
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$tuid", targetUserId);
        cmd.Parameters.AddWithValue("$tnick", targetNickname);
        cmd.Parameters.AddWithValue("$vuid", voterUserId);
        cmd.Parameters.AddWithValue("$vnick", voterNickname);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        using var upd = conn.CreateCommand();
        upd.CommandText = """
            UPDATE ban_vote_sessions SET vote_count = vote_count + 1
            WHERE id = $sid
            """;
        upd.Parameters.AddWithValue("$sid", sessionId);
        upd.ExecuteNonQuery();

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT vote_count FROM ban_vote_sessions WHERE id = $sid";
        countCmd.Parameters.AddWithValue("$sid", sessionId);
        return Convert.ToInt32(countCmd.ExecuteScalar());
    }

    public void CompleteSession(string sessionId)
    {
        var now = DateTime.Now.ToString("O");
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ban_vote_sessions SET status = 'completed', completed_at = $now WHERE id = $sid
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public List<BanVoteSession> ListActive()
    {
        var list = new List<BanVoteSession>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, target_user_id, target_nickname, vote_count, required_votes, status, created_at
            FROM ban_vote_sessions WHERE status = 'active' ORDER BY created_at DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadSession(reader));
        }
        return list;
    }

    private static BanVoteSession ReadSession(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        TargetUserId = reader.GetString(1),
        TargetNickname = reader.GetString(2),
        VoteCount = reader.GetInt32(3),
        RequiredVotes = reader.GetInt32(4),
        Status = reader.GetString(5),
        CreatedAt = DateTime.TryParse(reader.GetString(6), out var dt) ? dt : DateTime.Now
    };
}
