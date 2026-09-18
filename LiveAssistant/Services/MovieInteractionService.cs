using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 电影互动评分旁路模块：订阅 GiftReceived / 弹幕，独立账本，不侵入点歌与礼物积分。
/// </summary>
public sealed class MovieInteractionService : IDisposable
{
    private static readonly Regex ScoreDanmakuRegex = new(
        @"^(?<name>.+?)\s*(?<action>好评|差评)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ConfigManager _config;
    private readonly MovieInteractionRepository _repo;
    private readonly LogService _log;
    private readonly ConcurrentDictionary<string, object> _userLocks = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cleanupCts;
    private int _disposed;

    public MovieInteractionService(ConfigManager config, AppDatabase db, LogService log)
    {
        _config = config;
        _repo = new MovieInteractionRepository(db);
        _log = log;
    }

    public MovieInteractionRepository Repository => _repo;

    public void Start()
    {
        if (!_config.Settings.MovieInteraction.Enabled)
        {
            return;
        }

        StopCleanup();
        _cleanupCts = new CancellationTokenSource();
        _ = Task.Run(() => CleanupLoopAsync(_cleanupCts.Token));
        _log.Info("[MOVIE_SCORE_EXPIRE] MovieInteractionService 清理循环已启动");
    }

    public void StopCleanup()
    {
        try { _cleanupCts?.Cancel(); } catch { /* ignore */ }
        _cleanupCts = null;
    }

    /// <summary>
    /// 礼物被 GiftService 接受并抛出 GiftReceived 后调用。
    /// scorePoints = GiftEvent.Value * PointsPerDiamond（Value 已是总钻石，禁止再乘 Count）。
    /// </summary>
    public bool OnGiftReceived(GiftEvent gift)
    {
        if (!_config.Settings.MovieInteraction.Enabled)
        {
            return false;
        }

        if (gift == null || string.IsNullOrWhiteSpace(gift.EventId) || string.IsNullOrWhiteSpace(gift.UserId))
        {
            return false;
        }

        if (gift.Value <= 0)
        {
            return false;
        }

        var pointsPerDiamond = Math.Max(1, _config.Settings.MovieInteraction.PointsPerDiamond);
        var points = gift.Value * pointsPerDiamond;
        if (points <= 0)
        {
            return false;
        }

        if (_repo.CreditExistsByGiftEventId(gift.EventId))
        {
            _log.Info($"[MOVIE_SCORE_CREDIT] 跳过重复礼物积分 giftEventId={gift.EventId} userId={gift.UserId}");
            return false;
        }

        var expireSeconds = Math.Max(30, _config.Settings.MovieInteraction.CreditExpireSeconds);
        var created = gift.Time == default ? DateTime.Now : gift.Time;
        var credit = new MovieScoreCredit
        {
            GiftEventId = gift.EventId,
            UserId = gift.UserId,
            Nickname = gift.Nickname ?? "",
            GiftName = gift.GiftName ?? "",
            DiamondCount = gift.DiamondCount,
            Value = gift.Value,
            Points = points,
            CreatedAt = created,
            ExpiresAt = created.AddSeconds(expireSeconds),
            Status = "pending"
        };

        if (!_repo.TryInsertCredit(credit))
        {
            _log.Info($"[MOVIE_SCORE_CREDIT] 插入失败(可能重复) giftEventId={gift.EventId}");
            return false;
        }

        _log.Info(
            $"[MOVIE_SCORE_CREDIT] 生成可评分积分 giftEventId={gift.EventId} userId={gift.UserId} " +
            $"gift={gift.GiftName} diamonds={gift.Value} points={points} expiresAt={credit.ExpiresAt:O}");
        return true;
    }

    /// <summary>
    /// 旁路观察弹幕：写入事件流；若匹配「电影名 好评/差评」则尝试评分。
    /// 绝不截断调用方后续点歌/AI 逻辑。
    /// </summary>
    public void OnDanmaku(DanmakuItem item)
    {
        if (!_config.Settings.MovieInteraction.Enabled || item == null)
        {
            return;
        }

        if (!string.Equals(item.MsgType, "chat", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.MsgType))
        {
            return;
        }

        var content = (item.Content ?? "").Trim();
        if (content.Length == 0 || string.IsNullOrWhiteSpace(item.UserId))
        {
            return;
        }

        var createdAt = item.Timestamp == default ? DateTime.Now : item.Timestamp;
        try
        {
            _repo.AppendDanmakuStream(item.MsgId ?? "", new
            {
                type = "danmaku",
                msgId = item.MsgId ?? "",
                userId = item.UserId,
                nickname = item.Nickname ?? "",
                content,
                createdAt = createdAt.ToString("O"),
                platform = item.Platform ?? "douyin",
                roomId = item.RoomKey ?? ""
            }, createdAt);
        }
        catch (Exception ex)
        {
            _log.Error("movie_score", "[MOVIE_SCORE_API] 写入弹幕事件流失败", ex);
        }

        TryApplyScoreFromDanmaku(item, content, createdAt);
    }

    public bool TryApplyScoreFromDanmaku(DanmakuItem item, string? contentOverride = null, DateTime? nowOverride = null)
    {
        var content = (contentOverride ?? item.Content ?? "").Trim();
        var now = nowOverride ?? DateTime.Now;
        var match = ScoreDanmakuRegex.Match(content);
        if (!match.Success)
        {
            return false;
        }

        var movieQuery = match.Groups["name"].Value.Trim();
        var actionRaw = match.Groups["action"].Value;
        if (string.IsNullOrWhiteSpace(movieQuery))
        {
            return false;
        }

        var action = actionRaw == "差评" ? "bad" : "good";
        _log.Info(
            $"[MOVIE_SCORE_PARSE] msgId={item.MsgId} userId={item.UserId} content={Truncate(content, 80)} " +
            $"query={movieQuery} action={action}");

        var resolved = ResolveMovie(movieQuery);
        if (resolved.Ambiguous)
        {
            _log.Info($"[MOVIE_SCORE_PARSE] 电影名歧义 query={movieQuery} matches={resolved.MatchCount}，不消费积分");
            return false;
        }

        if (resolved.Entry == null)
        {
            _log.Info($"[MOVIE_SCORE_PARSE] 未识别电影 query={movieQuery}，不消费积分");
            return false;
        }

        var gate = _userLocks.GetOrAdd(item.UserId, _ => new object());
        lock (gate)
        {
            // 读取前顺带过期
            _repo.ExpirePendingCredits(now);

            var credits = _repo.GetPendingCreditsForUser(item.UserId, now);
            if (credits.Count == 0)
            {
                _log.Info($"[MOVIE_SCORE_APPLY] 无可用积分 userId={item.UserId} movie={resolved.Entry.MovieName}");
                return false;
            }

            var absolute = credits.Sum(c => c.Points);
            if (absolute <= 0)
            {
                return false;
            }

            var delta = action == "bad" ? -absolute : absolute;
            var before = _repo.GetTotalScore(resolved.Entry.MovieId);
            var eventId = Guid.NewGuid().ToString("N");
            var scoreEvent = new MovieScoreEvent
            {
                EventId = eventId,
                Platform = string.IsNullOrWhiteSpace(item.Platform) ? "douyin" : item.Platform,
                RoomId = item.RoomKey ?? "",
                UserId = item.UserId,
                Nickname = item.Nickname ?? "",
                MovieId = resolved.Entry.MovieId,
                MovieName = resolved.Entry.MovieName,
                Action = action,
                ScoreDelta = delta,
                AbsolutePoints = absolute,
                SourceGiftEventIds = credits.Select(c => c.GiftEventId).ToList(),
                CreatedAt = now,
                UploadStatus = "pending"
            };

            var streamPayloadFactory = (long totalAfterScore) => new
            {
                type = "movie_score",
                eventId,
                userId = scoreEvent.UserId,
                nickname = scoreEvent.Nickname,
                movieId = scoreEvent.MovieId,
                movieName = scoreEvent.MovieName,
                action,
                scoreDelta = delta,
                totalScore = totalAfterScore,
                absolutePoints = absolute,
                sourceGiftEventIds = scoreEvent.SourceGiftEventIds,
                createdAt = now.ToString("O"),
                platform = scoreEvent.Platform,
                roomId = scoreEvent.RoomId
            };

            if (!_repo.TryApplyScore(scoreEvent, credits.Select(c => c.Id).ToList(), streamPayloadFactory, out var totalAfter, out var reason))
            {
                _log.Info(
                    $"[MOVIE_SCORE_APPLY] 事务未成功 userId={item.UserId} reason={reason} " +
                    $"movie={resolved.Entry.MovieName}");
                return false;
            }

            _log.Info(
                $"[MOVIE_SCORE_APPLY] 评分成功 eventId={eventId} userId={item.UserId} " +
                $"movie={resolved.Entry.MovieName}({resolved.Entry.MovieId}) action={action} " +
                $"delta={delta} before={before} after={totalAfter} " +
                $"consumedGiftEventIds=[{string.Join(",", scoreEvent.SourceGiftEventIds)}]");
            return true;
        }
    }

    public object GetHealth()
    {
        var now = DateTime.Now;
        return new
        {
            ok = true,
            enabled = _config.Settings.MovieInteraction.Enabled,
            movieCount = _repo.CountCatalog(),
            pendingCreditCount = _repo.CountPendingCredits(now),
            pendingUploadCount = _repo.CountPendingUploads()
        };
    }

    public object UpdateCatalog(MovieCatalogUpdateRequest request)
    {
        var now = DateTime.Now;
        var movies = (request.Movies ?? new List<MovieCatalogUpdateItem>())
            .Where(m => !string.IsNullOrWhiteSpace(m.MovieId) && !string.IsNullOrWhiteSpace(m.MovieName))
            .Select(m => new MovieCatalogEntry
            {
                MovieId = m.MovieId.Trim(),
                MovieName = m.MovieName.Trim(),
                Aliases = (m.Aliases ?? new List<string>())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Rank = m.Rank,
                UpdatedAt = now
            })
            .ToList();

        _repo.ReplaceCatalog(movies);
        _log.Info($"[MOVIE_SCORE_API] 更新电影目录 count={movies.Count}");
        return new { ok = true, count = movies.Count };
    }

    public object GetScores()
    {
        var movies = _repo.GetTotals().Select(t => new
        {
            movieId = t.MovieId,
            movieName = t.MovieName,
            score = t.Score,
            goodUserCount = t.GoodUserCount,
            badUserCount = t.BadUserCount
        }).ToList();
        return new { ok = true, movies };
    }

    public object GetEvents(long after, int limit = 200)
    {
        var items = _repo.GetStreamAfter(after, limit);
        var events = new List<object>();
        long maxSeq = after;
        foreach (var item in items)
        {
            maxSeq = Math.Max(maxSeq, item.Seq);
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(item.PayloadJson) ? "{}" : item.PayloadJson);
                var root = doc.RootElement.Clone();
                events.Add(new
                {
                    seq = item.Seq,
                    type = item.Type,
                    createdAt = item.CreatedAt.ToString("O"),
                    data = root
                });
            }
            catch
            {
                events.Add(new
                {
                    seq = item.Seq,
                    type = item.Type,
                    createdAt = item.CreatedAt.ToString("O"),
                    data = (object?)null,
                    raw = item.PayloadJson
                });
            }
        }

        return new { ok = true, after, cursor = maxSeq, events };
    }

    public (MovieCatalogEntry? Entry, bool Ambiguous, int MatchCount) ResolveMovie(string query)
    {
        var catalog = _repo.GetCatalog();
        if (catalog.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return (null, false, 0);
        }

        var q = NormalizeName(query);
        var matches = new List<MovieCatalogEntry>();
        foreach (var m in catalog)
        {
            if (NormalizeName(m.MovieName) == q)
            {
                matches.Add(m);
                continue;
            }

            foreach (var alias in m.Aliases ?? new List<string>())
            {
                if (NormalizeName(alias) == q)
                {
                    matches.Add(m);
                    break;
                }
            }
        }

        matches = matches
            .GroupBy(m => m.MovieId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (matches.Count == 0)
        {
            return (null, false, 0);
        }

        if (matches.Count > 1)
        {
            return (null, true, matches.Count);
        }

        return (matches[0], false, 1);
    }

    private static string NormalizeName(string name)
        => (name ?? "").Replace(" ", "", StringComparison.Ordinal).Trim();

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var interval = Math.Clamp(_config.Settings.MovieInteraction.CleanupIntervalSeconds, 30, 60);
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
                if (!_config.Settings.MovieInteraction.Enabled)
                {
                    continue;
                }

                var n = _repo.ExpirePendingCredits(DateTime.Now);
                if (n > 0)
                {
                    _log.Info($"[MOVIE_SCORE_EXPIRE] 标记过期积分 count={n}");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("movie_score", "[MOVIE_SCORE_EXPIRE] 清理异常", ex);
            }
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopCleanup();
    }
}
