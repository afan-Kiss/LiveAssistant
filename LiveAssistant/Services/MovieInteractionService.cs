using System.Collections.Concurrent;
using System.Text.Json;
using LiveAssistant.Config;
using LiveAssistant.Database;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 电影互动评分旁路模块：订阅 GiftReceived / 弹幕，独立账本，不侵入点歌与礼物积分。
/// 主动提醒一律走 ReplyQueue，发送失败不回滚评分。
/// </summary>
public sealed class MovieInteractionService : IDisposable
{
    // 长词优先，避免「不好看」被「好看」截断
    private static readonly string[] ScoreActions = ["不好看", "差评", "好看", "好评"];

    private const int MaxDanmakuChars = 45;
    private const string NotifyFailTag = "MOVIE_INTERACTION_NOTIFY_SEND_FAILED";

    private readonly ConfigManager _config;
    private readonly MovieInteractionRepository _repo;
    private readonly LogService _log;
    private readonly ChatAudienceFilter? _audienceFilter;
    private readonly ConcurrentDictionary<string, object> _userLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _userCreditMovieHints = new(StringComparer.Ordinal);
    private readonly object _notifyLock = new();
    private readonly Dictionary<string, DateTime> _giftGuideCooldownUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _hintCooldownUtc = new(StringComparer.Ordinal);
    private ReplyQueue? _replyQueue;
    private CancellationTokenSource? _cleanupCts;
    private int _disposed;

    /// <summary>测试可注入时钟；生产默认 DateTime.Now。</summary>
    internal Func<DateTime>? NowProvider { get; set; }

    public MovieInteractionService(
        ConfigManager config,
        AppDatabase db,
        LogService log,
        ReplyQueue? replyQueue = null,
        ChatAudienceFilter? audienceFilter = null)
    {
        _config = config;
        _repo = new MovieInteractionRepository(db);
        _log = log;
        _replyQueue = replyQueue;
        _audienceFilter = audienceFilter;
    }

    private DateTime Now() => NowProvider?.Invoke() ?? DateTime.Now;

    public MovieInteractionRepository Repository => _repo;

    public void BindReplyQueue(ReplyQueue replyQueue)
        => _replyQueue = replyQueue;

    public void Start()
    {
        if (!_config.Settings.MovieInteraction.Enabled)
        {
            return;
        }

        ApplyGlobalSendInterval();
        StopCleanup();
        _cleanupCts = new CancellationTokenSource();
        _ = Task.Run(() => CleanupLoopAsync(_cleanupCts.Token));
        _log.Info("[MOVIE_SCORE_EXPIRE] MovieInteractionService 清理循环已启动");
    }

    /// <summary>把电影提醒的全局发送间隔同步到 ReplyQueue 设置。</summary>
    public void ApplyGlobalSendInterval()
    {
        var ms = _config.Settings.MovieInteraction.Notification?.GlobalSendIntervalMs ?? 0;
        if (ms > 0)
        {
            _config.Settings.Reply.MinIntervalMs = Math.Max(_config.Settings.Reply.MinIntervalMs, ms);
        }
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
        // 有效期从本机成功登记 credit 起算，不用平台 gift.Time 当过期起点
        var receivedAt = Now();
        var credit = new MovieScoreCredit
        {
            GiftEventId = gift.EventId,
            UserId = gift.UserId,
            Nickname = gift.Nickname ?? "",
            GiftName = gift.GiftName ?? "",
            DiamondCount = gift.DiamondCount,
            Value = gift.Value,
            Points = points,
            CreatedAt = receivedAt,
            ExpiresAt = receivedAt.AddSeconds(expireSeconds),
            Status = "pending"
        };

        if (!_repo.TryInsertCredit(credit))
        {
            _log.Info($"[MOVIE_SCORE_CREDIT] 插入失败(可能重复) giftEventId={gift.EventId}");
            return false;
        }

        var movieHint = PickShortMovieExample();
        if (!string.IsNullOrWhiteSpace(movieHint))
        {
            _userCreditMovieHints[gift.UserId] = movieHint;
        }

        _log.Info(
            $"[MOVIE_SCORE_CREDIT] 生成可评分积分 giftEventId={gift.EventId} userId={gift.UserId} " +
            $"gift={gift.GiftName} diamonds={gift.Value} points={points} expiresAt={credit.ExpiresAt:O} " +
            $"movieHint={movieHint}");

        TryEnqueueGiftScoreGuide(gift, movieHint);
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

        if (!ChatMessageFilter.IsAudienceChat(item))
        {
            return;
        }

        var content = (item.Content ?? "").Trim();
        if (content.Length == 0 || string.IsNullOrWhiteSpace(item.UserId))
        {
            return;
        }

        var createdAt = item.Timestamp == default ? DateTime.Now : item.Timestamp;

        // 评分旁路优先：即使后续被词云/机器人过滤，送礼观众的评分指令仍应处理
        TryApplyScoreFromDanmaku(item, content, createdAt);

        var ctx = _audienceFilter?.Snapshot(item.RoomKey) ?? new ChatMessageFilterContext();
        var classification = ChatMessageFilter.Classify(item, ctx);
        if (classification.Kind is ChatMessageKind.SystemMessage
            or ChatMessageKind.BotMessage
            or ChatMessageKind.StreamerMessage)
        {
            _log.Info(
                $"[MOVIE_SCORE_STREAM_SKIP] msgId={item.MsgId} userId={item.UserId} " +
                $"kind={classification.Kind} reason={classification.Reason}");
            return;
        }

        if (classification.Kind == ChatMessageKind.NormalChat)
        {
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
        }
        else
        {
            _log.Info(
                $"[WORD_CLOUD_SKIP] msgId={item.MsgId} userId={item.UserId} " +
                $"kind={classification.Kind} reason={classification.Reason} content={Truncate(content, 40)}");
        }
    }

    public bool TryApplyScoreFromDanmaku(DanmakuItem item, string? contentOverride = null, DateTime? nowOverride = null)
    {
        var content = (contentOverride ?? item.Content ?? "").Trim();
        var now = nowOverride ?? DateTime.Now;

        if (_audienceFilter != null)
        {
            var filter = _audienceFilter.Evaluate(item);
            if (filter.Excluded && filter.Reason is "outbound_msg_id" or "outbound_content" or "bot_template")
            {
                return false;
            }
        }

        if (!TryParseScoreDanmaku(content, out var movieQuery, out var actionRaw))
        {
            return false;
        }

        var action = MapScoreAction(actionRaw);
        var standalone = string.IsNullOrWhiteSpace(movieQuery);
        if (standalone)
        {
            movieQuery = ResolveCreditContextMovie(item.UserId, now);
        }

        _log.Info(
            $"[MOVIE_SCORE_PARSE] msgId={item.MsgId} userId={item.UserId} " +
            $"content={Truncate(content, 80)} query={movieQuery} action={action} standalone={standalone}");

        if (string.IsNullOrWhiteSpace(movieQuery))
        {
            _log.Info($"[MOVIE_SCORE_PARSE] 仅动作词但无可用电影上下文 userId={item.UserId}");
            return false;
        }

        var resolved = ResolveMovie(movieQuery);
        if (resolved.Ambiguous)
        {
            _log.Info(
                $"[MOVIE_SCORE_PARSE] 电影名歧义 query={movieQuery} matches={resolved.MatchCount} " +
                $"matchType={resolved.MatchType}，不消费积分");
            TryEnqueueInvalidHint(item, "ambiguous");
            return false;
        }

        if (resolved.Entry == null)
        {
            _log.Info(
                $"[MOVIE_SCORE_PARSE] 未识别电影 query={movieQuery} reason={resolved.FailReason}，不消费积分");
            TryEnqueueInvalidHint(item, "not_found");
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
                var hintKind = _repo.HasRecentlyExpiredCredit(item.UserId, now, TimeSpan.FromMinutes(10))
                    ? "expired"
                    : "no_credit";
                TryEnqueueInvalidHint(item, hintKind);
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
            _log.Info(
                $"[MOVIE_SCORE_CREDIT_USED] userId={item.UserId} movieId={resolved.Entry.MovieId} " +
                $"credits={credits.Count} points={absolute} action={action}");

            TryEnqueueScoreSuccessReply(item, scoreEvent, totalAfter);
            return true;
        }
    }

    public object GetHealth()
    {
        var now = DateTime.Now;
        var creditExpireSeconds = Math.Max(30, _config.Settings.MovieInteraction.CreditExpireSeconds);
        return new
        {
            ok = true,
            enabled = _config.Settings.MovieInteraction.Enabled,
            movieCount = _repo.CountCatalog(),
            pendingCreditCount = _repo.CountPendingCredits(now),
            pendingUploadCount = _repo.CountPendingUploads(),
            creditExpireSeconds,
            creditExpireMinutes = Math.Max(1, creditExpireSeconds / 60)
        };
    }

    public object UpdateCatalog(MovieCatalogUpdateRequest request)
    {
        var now = DateTime.Now;
        var movies = (request.Movies ?? new List<MovieCatalogUpdateItem>())
            .Where(m => !string.IsNullOrWhiteSpace(m.MovieId) && !string.IsNullOrWhiteSpace(m.MovieName))
            .Select(m =>
            {
                var name = m.MovieName.Trim();
                return new MovieCatalogEntry
                {
                    MovieId = m.MovieId.Trim(),
                    MovieName = name,
                    Aliases = MergeAliases(name, m.Aliases),
                    Rank = m.Rank,
                    UpdatedAt = now
                };
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
        var serverMaxSeq = _repo.GetStreamMaxSeq();
        var streamEpoch = _repo.GetStreamEpoch();
        var reset = after > serverMaxSeq;
        if (reset)
        {
            _log.Info(
                $"[MOVIE_EVENT_CURSOR_RESET] clientAfter={after} serverMaxSeq={serverMaxSeq} " +
                $"streamEpoch={streamEpoch} → cursor=0");
            return new
            {
                ok = true,
                after,
                cursor = 0L,
                serverMaxSeq,
                streamEpoch,
                reset = true,
                events = Array.Empty<object>()
            };
        }

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

        return new
        {
            ok = true,
            after,
            cursor = maxSeq,
            serverMaxSeq,
            streamEpoch,
            reset = false,
            events
        };
    }

    public MovieResolveResult ResolveMovie(string query)
    {
        var catalog = _repo.GetCatalog();
        var rawQuery = (query ?? "").Trim();
        if (catalog.Count == 0)
        {
            LogResolveFailure(rawQuery, 0, "empty_catalog", catalog);
            return MovieResolveResult.Fail("empty_catalog");
        }

        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            LogResolveFailure(rawQuery, catalog.Count, "empty_query", catalog);
            return MovieResolveResult.Fail("empty_query");
        }

        // 1) movieId 精准匹配
        var idHits = catalog
            .Where(m => m.MovieId.Equals(rawQuery, StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.MovieId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (idHits.Count == 1)
        {
            return LogResolveSuccess(rawQuery, idHits[0], "movieId");
        }

        if (idHits.Count > 1)
        {
            LogResolveFailure(rawQuery, catalog.Count, "ambiguous_movieId", catalog, idHits.Count);
            return MovieResolveResult.AmbiguousResult(idHits.Count, "movieId");
        }

        var qNorm = NormalizeName(rawQuery);
        if (qNorm.Length == 0)
        {
            LogResolveFailure(rawQuery, catalog.Count, "empty_normalized_query", catalog);
            return MovieResolveResult.Fail("empty_normalized_query");
        }

        // 2) alias 精准匹配（去符号后相等）
        var aliasHits = FindExactAliasMatches(catalog, qNorm);
        if (aliasHits.Count == 1)
        {
            return LogResolveSuccess(rawQuery, aliasHits[0], "alias");
        }

        if (aliasHits.Count > 1)
        {
            LogResolveFailure(rawQuery, catalog.Count, "ambiguous_alias", catalog, aliasHits.Count);
            return MovieResolveResult.AmbiguousResult(aliasHits.Count, "alias");
        }

        // 3) movieName 精准匹配（原始去空白后忽略大小写）
        var nameExactHits = catalog
            .Where(m => m.MovieName.Trim().Equals(rawQuery, StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.MovieId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (nameExactHits.Count == 1)
        {
            return LogResolveSuccess(rawQuery, nameExactHits[0], "movieName");
        }

        if (nameExactHits.Count > 1)
        {
            LogResolveFailure(rawQuery, catalog.Count, "ambiguous_movieName", catalog, nameExactHits.Count);
            return MovieResolveResult.AmbiguousResult(nameExactHits.Count, "movieName");
        }

        // 4) 去除符号后匹配（片名规范化相等，或规范化后的自动拆片段相等）
        var strippedHits = FindNormalizedExactMatches(catalog, qNorm);
        if (strippedHits.Count == 1)
        {
            return LogResolveSuccess(rawQuery, strippedHits[0], "normalized");
        }

        if (strippedHits.Count > 1)
        {
            LogResolveFailure(rawQuery, catalog.Count, "ambiguous_normalized", catalog, strippedHits.Count);
            return MovieResolveResult.AmbiguousResult(strippedHits.Count, "normalized");
        }

        // 5) 包含关系（最后手段）：仅「片名包含查询」，且唯一；禁止查询包含片名（避免「奥德赛续集」误中「奥德赛」）
        var containHits = FindSafeContainmentMatches(catalog, qNorm);
        if (containHits.Count == 1)
        {
            return LogResolveSuccess(rawQuery, containHits[0], "contains");
        }

        if (containHits.Count > 1)
        {
            LogResolveFailure(rawQuery, catalog.Count, "ambiguous_contains", catalog, containHits.Count);
            return MovieResolveResult.AmbiguousResult(containHits.Count, "contains");
        }

        LogResolveFailure(rawQuery, catalog.Count, "not_found", catalog);
        return MovieResolveResult.Fail("not_found");
    }

    private MovieResolveResult LogResolveSuccess(string query, MovieCatalogEntry entry, string matchType)
    {
        _log.Info(
            $"[MOVIE_RESOLVE_SUCCESS] query={Truncate(query, 80)} movieId={entry.MovieId} " +
            $"movieName={Truncate(entry.MovieName, 80)} matchType={matchType}");
        return MovieResolveResult.Ok(entry, matchType);
    }

    private void LogResolveFailure(
        string query,
        int catalogCount,
        string reason,
        IReadOnlyList<MovieCatalogEntry> catalog,
        int matchCount = 0)
    {
        var preview = string.Join(
            " | ",
            catalog
                .OrderBy(m => m.Rank <= 0 ? int.MaxValue : m.Rank)
                .ThenBy(m => m.MovieName, StringComparer.Ordinal)
                .Take(10)
                .Select(m =>
                {
                    var aliasPreview = string.Join(",", (m.Aliases ?? new List<string>()).Take(3));
                    return $"{m.Rank}:{m.MovieId}:{Truncate(m.MovieName, 24)}" +
                           (string.IsNullOrWhiteSpace(aliasPreview) ? "" : $"[{aliasPreview}]");
                }));
        _log.Info(
            $"[MOVIE_RESOLVE_FAIL] query={Truncate(query, 80)} count={catalogCount} " +
            $"reason={reason} matchCount={matchCount} candidates=[{preview}]");
    }

    private static List<MovieCatalogEntry> FindExactAliasMatches(
        IReadOnlyList<MovieCatalogEntry> catalog,
        string normalizedQuery)
    {
        var hits = new List<MovieCatalogEntry>();
        foreach (var m in catalog)
        {
            foreach (var alias in m.Aliases ?? new List<string>())
            {
                if (NormalizeName(alias).Equals(normalizedQuery, StringComparison.Ordinal))
                {
                    hits.Add(m);
                    break;
                }
            }
        }

        return DedupByMovieId(hits);
    }

    private static List<MovieCatalogEntry> FindNormalizedExactMatches(
        IReadOnlyList<MovieCatalogEntry> catalog,
        string normalizedQuery)
    {
        var hits = new List<MovieCatalogEntry>();
        foreach (var m in catalog)
        {
            var nameNorm = NormalizeName(m.MovieName);
            if (nameNorm.Equals(normalizedQuery, StringComparison.Ordinal))
            {
                hits.Add(m);
                continue;
            }

            // 去符号后，片名拆分段与查询相等（覆盖「杀死比尔：血色全传」↔「杀死比尔」在 aliases 未落库时的兜底）
            foreach (var part in ExpandTitleParts(m.MovieName))
            {
                if (NormalizeName(part).Equals(normalizedQuery, StringComparison.Ordinal))
                {
                    hits.Add(m);
                    break;
                }
            }
        }

        return DedupByMovieId(hits);
    }

    private static List<MovieCatalogEntry> FindSafeContainmentMatches(
        IReadOnlyList<MovieCatalogEntry> catalog,
        string normalizedQuery)
    {
        // 短查询太容易误伤，至少 2 字；更长查询才允许子串包含
        if (normalizedQuery.Length < 2)
        {
            return new List<MovieCatalogEntry>();
        }

        var hits = new List<(MovieCatalogEntry Entry, int Score)>();
        foreach (var m in catalog)
        {
            var nameNorm = NormalizeName(m.MovieName);
            if (nameNorm.Length == 0)
            {
                continue;
            }

            // 禁止反向包含：查询更长且包含片名时不匹配
            if (normalizedQuery.Length > nameNorm.Length
                && normalizedQuery.Contains(nameNorm, StringComparison.Ordinal))
            {
                continue;
            }

            if (!nameNorm.Contains(normalizedQuery, StringComparison.Ordinal))
            {
                continue;
            }

            // 覆盖率：查询占片名比例越高越可信；过短子串相对长片名时丢弃
            var coverage = (double)normalizedQuery.Length / nameNorm.Length;
            if (normalizedQuery.Length < 3 && coverage < 0.5)
            {
                continue;
            }

            if (coverage < 0.25 && normalizedQuery.Length < 4)
            {
                continue;
            }

            hits.Add((m, normalizedQuery.Length));
        }

        if (hits.Count == 0)
        {
            return new List<MovieCatalogEntry>();
        }

        // 多部电影同时包含同一查询时：取查询覆盖更长者；仍并列则歧义
        var bestLen = hits.Max(h => h.Score);
        var best = hits.Where(h => h.Score == bestLen).Select(h => h.Entry).ToList();
        return DedupByMovieId(best);
    }

    private static List<MovieCatalogEntry> DedupByMovieId(List<MovieCatalogEntry> hits)
        => hits
            .GroupBy(m => m.MovieId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

    /// <summary>
    /// 合并客户端别名与由片名自动拆出的别名；不改写原始 movieName。
    /// </summary>
    internal static List<string> MergeAliases(string movieName, IEnumerable<string>? provided)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0)
            {
                return;
            }

            // 不把完整原名再塞进 aliases
            if (v.Equals(movieName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (seen.Add(v))
            {
                result.Add(v);
            }
        }

        foreach (var a in provided ?? Array.Empty<string>())
        {
            Add(a);
        }

        foreach (var part in ExpandTitleParts(movieName))
        {
            Add(part);
        }

        return result;
    }

    /// <summary>
    /// 从正式片名拆出可检索片段：冒号前后、空格分段、去括号后的主干等。
    /// </summary>
    internal static IEnumerable<string> ExpandTitleParts(string movieName)
    {
        var name = (movieName ?? "").Trim();
        if (name.Length == 0)
        {
            yield break;
        }

        yield return StripBracketSegments(name);

        foreach (var piece in SplitTitleDelimiters(name))
        {
            yield return piece;
            yield return StripBracketSegments(piece);
        }

        // 去括号后的主干再按分隔符拆一次
        var stripped = StripBracketSegments(name);
        if (!stripped.Equals(name, StringComparison.Ordinal))
        {
            foreach (var piece in SplitTitleDelimiters(stripped))
            {
                yield return piece;
            }
        }
    }

    private static IEnumerable<string> SplitTitleDelimiters(string name)
    {
        var parts = name.Split(
            new[] { '：', ':', ' ', '\t', '/', '／', '|', '｜' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            if (p.Length > 0)
            {
                yield return p;
            }
        }
    }

    /// <summary>去掉 （）()【】[]《》 及其内部内容，保留主干。</summary>
    internal static string StripBracketSegments(string name)
    {
        var text = name ?? "";
        if (text.Length == 0)
        {
            return "";
        }

        Span<char> buffer = text.Length <= 128 ? stackalloc char[text.Length] : new char[text.Length];
        var n = 0;
        var depth = 0;
        foreach (var c in text)
        {
            if (c is '(' or '（' or '[' or '【' or '《')
            {
                depth++;
                continue;
            }

            if (c is ')' or '）' or ']' or '】' or '》')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            buffer[n++] = c;
        }

        return n == 0 ? "" : new string(buffer[..n]).Trim();
    }

    private void TryEnqueueGiftScoreGuide(GiftEvent gift, string? movieHint = null)
    {
        var notify = _config.Settings.MovieInteraction.Notification;
        if (notify == null || !notify.Enabled || _replyQueue == null)
        {
            return;
        }

        var room = gift.RoomKey?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(gift.UserId))
        {
            return;
        }

        var cooldownSec = Math.Max(1, notify.GiftGuideUserCooldownSeconds);
        var nowUtc = DateTime.UtcNow;
        lock (_notifyLock)
        {
            PurgeCooldownLocked(_giftGuideCooldownUtc, nowUtc, TimeSpan.FromMinutes(30));
            if (_giftGuideCooldownUtc.TryGetValue(gift.UserId, out var last)
                && nowUtc - last < TimeSpan.FromSeconds(cooldownSec))
            {
                _log.Info(
                    $"[MOVIE_SCORE_NOTIFY] gift guide skipped cooldown userId={gift.UserId} " +
                    $"remainSec={(cooldownSec - (nowUtc - last).TotalSeconds):F0}");
                return;
            }

            _giftGuideCooldownUtc[gift.UserId] = nowUtc;
        }

        var minutes = Math.Max(1, _config.Settings.MovieInteraction.CreditExpireSeconds / 60);
        var example = string.IsNullOrWhiteSpace(movieHint) ? PickShortMovieExample() : movieHint;
        var msg = string.IsNullOrWhiteSpace(example)
            ? $"感谢你的礼物❤️ 已获得电影评分机会，{minutes}分钟内发送「电影名 好看」或「电影名 不好看」即可参与评分"
            : $"感谢礼物❤️ {minutes}分钟内发送「{example} 好看」或「{example} 不好看」即可参与电影评分";
        msg = ClampDanmaku(msg);

        _replyQueue.EnqueueMention(
            room,
            gift.UserId,
            msg,
            nickname: gift.Nickname,
            failureLogTag: NotifyFailTag);
        _log.Info($"[MOVIE_SCORE_NOTIFY] gift guide enqueued userId={gift.UserId} room={room}");
    }

    private void TryEnqueueScoreSuccessReply(DanmakuItem item, MovieScoreEvent scoreEvent, long totalAfter)
    {
        var notify = _config.Settings.MovieInteraction.Notification;
        if (notify == null || !notify.Enabled || !notify.ScoreSuccessReplyEnabled || _replyQueue == null)
        {
            return;
        }

        var room = item.RoomKey?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(item.UserId))
        {
            return;
        }

        var shortName = ShortMovieName(scoreEvent.MovieName);
        string msg;
        if (scoreEvent.Action == "bad")
        {
            msg = $"《{shortName}》不好看 {scoreEvent.ScoreDelta}分";
        }
        else
        {
            var deltaText = scoreEvent.ScoreDelta >= 0 ? $"+{scoreEvent.ScoreDelta}" : scoreEvent.ScoreDelta.ToString();
            msg = $"《{shortName}》好看 {deltaText}分❤️";
        }

        var withTotal = ClampDanmaku($"{msg} 当前总分 {totalAfter}");
        msg = withTotal.Length <= MaxDanmakuChars ? withTotal : ClampDanmaku(msg);

        _replyQueue.EnqueueMention(
            room,
            item.UserId,
            msg,
            nickname: item.Nickname,
            failureLogTag: NotifyFailTag);
        _log.Info(
            $"[MOVIE_SCORE_NOTIFY] score success reply enqueued userId={item.UserId} " +
            $"delta={scoreEvent.ScoreDelta} total={totalAfter}");
    }

    private void TryEnqueueInvalidHint(DanmakuItem item, string kind)
    {
        var notify = _config.Settings.MovieInteraction.Notification;
        if (notify == null || !notify.Enabled || !notify.InvalidScoreHintEnabled || _replyQueue == null)
        {
            return;
        }

        var room = item.RoomKey?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(item.UserId))
        {
            return;
        }

        var cooldownSec = Math.Max(1, notify.GiftGuideUserCooldownSeconds);
        var cooldownKey = $"{item.UserId}|{kind}";
        var nowUtc = DateTime.UtcNow;
        lock (_notifyLock)
        {
            PurgeCooldownLocked(_hintCooldownUtc, nowUtc, TimeSpan.FromMinutes(30));
            if (_hintCooldownUtc.TryGetValue(cooldownKey, out var last)
                && nowUtc - last < TimeSpan.FromSeconds(cooldownSec))
            {
                return;
            }

            _hintCooldownUtc[cooldownKey] = nowUtc;
        }

        var minutes = Math.Max(1, _config.Settings.MovieInteraction.CreditExpireSeconds / 60);
        var msg = kind switch
        {
            "ambiguous" => "电影名不够明确，请发送完整片名 + 好看/不好看",
            "not_found" => "没找到这部电影，请使用榜单上的电影名 + 好看/不好看",
            "expired" => $"这次评分资格已过期，请先送礼再评分～送礼后{minutes}分钟内有效",
            _ => $"你还没有送礼，需要先送礼才能评分哦～送礼后{minutes}分钟内可发「电影名 好看/不好看」"
        };
        msg = ClampDanmaku(msg);

        _replyQueue.EnqueueMention(
            room,
            item.UserId,
            msg,
            nickname: item.Nickname,
            failureLogTag: NotifyFailTag);
        _log.Info($"[MOVIE_SCORE_NOTIFY] invalid hint kind={kind} userId={item.UserId}");
    }

    private string PickShortMovieExample()
    {
        var catalog = _repo.GetCatalog()
            .OrderBy(m => m.Rank <= 0 ? int.MaxValue : m.Rank)
            .ThenBy(m => m.MovieName, StringComparer.Ordinal)
            .ToList();
        if (catalog.Count == 0)
        {
            return "";
        }

        foreach (var m in catalog.Take(10))
        {
            var alias = (m.Aliases ?? new List<string>())
                .Select(a => a.Trim())
                .Where(a => a.Length is > 0 and <= 8)
                .OrderBy(a => a.Length)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(alias))
            {
                return alias;
            }

            var name = ShortMovieName(m.MovieName);
            if (name.Length <= 8)
            {
                return name;
            }
        }

        return ShortMovieName(catalog[0].MovieName);
    }

    private static string ShortMovieName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length <= 10)
        {
            return name;
        }

        return name[..10];
    }

    private static string ClampDanmaku(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length <= MaxDanmakuChars)
        {
            return text;
        }

        return text[..MaxDanmakuChars];
    }

    private static void PurgeCooldownLocked(Dictionary<string, DateTime> map, DateTime nowUtc, TimeSpan keep)
    {
        if (map.Count < 500)
        {
            return;
        }

        foreach (var dead in map.Where(kv => nowUtc - kv.Value > keep).Select(kv => kv.Key).ToList())
        {
            map.Remove(dead);
        }
    }

    /// <summary>
    /// 支持：哪吒好评 / 哪吒 好看 / 《哪吒》差评 / 给哪吒不好看 / 单独「好评」「好看」（需后续结合 credit 上下文电影）。
    /// </summary>
    internal static bool TryParseScoreDanmaku(string content, out string movieQuery, out string actionRaw)
    {
        movieQuery = "";
        actionRaw = "";
        var text = NormalizeScoreInput(content);
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var candidate in ScoreActions)
        {
            if (!text.Equals(candidate, StringComparison.Ordinal))
            {
                continue;
            }

            actionRaw = candidate;
            return true;
        }

        var action = "";
        foreach (var candidate in ScoreActions)
        {
            if (!text.EndsWith(candidate, StringComparison.Ordinal))
            {
                continue;
            }

            action = candidate;
            text = text[..^candidate.Length];
            break;
        }

        if (action.Length == 0)
        {
            return false;
        }

        text = StripMovieQuery(text);
        actionRaw = action;
        movieQuery = text;
        return true;
    }

    private static string NormalizeScoreInput(string? content)
    {
        var text = (content ?? "").Trim();
        if (text.Length == 0)
        {
            return "";
        }

        while (text.StartsWith('@'))
        {
            var space = text.IndexOf(' ');
            if (space <= 0)
            {
                break;
            }

            text = text[(space + 1)..].Trim();
        }

        return text.Trim('。', '.', '!', '！', '?', '？', '~', '～', ' ');
    }

    private static string StripMovieQuery(string text)
    {
        text = text.Trim();
        text = text.TrimEnd(' ', '\t', ':', '：', ',', '，', '、', '-', '—', '–', '/', '／', '的', '。', '.', '!', '！', '?', '？');
        text = text.Trim().Trim('《', '》', '「', '」', '『', '』', '"', '“', '”', '\'', '‘', '’');
        text = text.Trim();
        if (text.StartsWith("给", StringComparison.Ordinal) || text.StartsWith("为", StringComparison.Ordinal))
        {
            text = text[1..].Trim().Trim('《', '》', '「', '」', '『', '』', '"', '“', '”');
        }

        return text.Trim();
    }

    private static string MapScoreAction(string actionRaw)
        => actionRaw is "差评" or "不好看" ? "bad" : "good";

    private string ResolveCreditContextMovie(string userId, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return "";
        }

        if (_userCreditMovieHints.TryGetValue(userId, out var hint) && !string.IsNullOrWhiteSpace(hint))
        {
            return hint.Trim();
        }

        _repo.ExpirePendingCredits(now);
        if (_repo.GetPendingCreditsForUser(userId, now).Count == 0)
        {
            return "";
        }

        var catalog = _repo.GetCatalog()
            .OrderBy(m => m.Rank <= 0 ? int.MaxValue : m.Rank)
            .ThenBy(m => m.MovieName, StringComparer.Ordinal)
            .FirstOrDefault();
        return catalog == null ? "" : ShortMovieName(catalog.MovieName);
    }

    private static string NormalizeName(string name)
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0)
        {
            return "";
        }

        Span<char> buffer = text.Length <= 128 ? stackalloc char[text.Length] : new char[text.Length];
        var n = 0;
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            if (IsIgnorableMovieNamePunctuation(c))
            {
                continue;
            }

            buffer[n++] = c;
        }

        return n == 0 ? "" : new string(buffer[..n]);
    }

    /// <summary>片名模糊匹配：去标点空格后相等，或榜单名以查询为前缀（查询至少 2 字）。保留供单测/兼容。</summary>
    internal static bool NamesMatch(string normalizedCatalogName, string normalizedQuery)
    {
        if (normalizedCatalogName.Length == 0 || normalizedQuery.Length == 0)
        {
            return false;
        }

        if (normalizedCatalogName.Equals(normalizedQuery, StringComparison.Ordinal))
        {
            return true;
        }

        // 观众常漏打标点/尾字：榜单名以查询开头（至少 2 字）。禁止反向前缀，避免「奥德赛续集」误中「奥德赛」
        if (normalizedQuery.Length >= 2
            && normalizedCatalogName.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsIgnorableMovieNamePunctuation(char c)
        => c is '!' or '！' or ':' or '：' or '?' or '？' or '.' or '。' or ',' or '，' or '、'
            or '…' or '·' or '•' or '-' or '—' or '–' or '/' or '／' or '~' or '～';

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
