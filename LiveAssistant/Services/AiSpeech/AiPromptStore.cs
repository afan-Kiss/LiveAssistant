using System.Security.Cryptography;
using System.Text;

namespace LiveAssistant.Services.AiSpeech;

/// <summary>
/// 提示词仓库：Config/AiSpeech 为主，data/AiSpeech 可覆盖；热重载。
/// </summary>
public sealed class AiPromptStore : IDisposable
{
    public enum PromptKind
    {
        Personality,
        Danmaku,
        Gift,
        Welcome,
        Summary,
        Like,
        SongRequest
    }

    private static readonly object GlobalLock = new();
    private static AiPromptStore? _shared;

    private readonly object _gate = new();
    private readonly string _configDir;
    private readonly string _dataDir;
    private readonly Action<string>? _log;
    private readonly Dictionary<PromptKind, PromptSnapshot> _cache = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _debounceCts;
    private DateTime _loadedAtUtc = DateTime.UtcNow;
    private string _version = "";
    private int _disposed;

    private sealed class PromptSnapshot
    {
        public string Text { get; set; } = "";
        public string Version { get; set; } = "";
        public DateTime MtimeUtc { get; set; }
        public string Path { get; set; } = "";
    }

    public AiPromptStore(string? configDir = null, string? dataDir = null, Action<string>? log = null)
    {
        _configDir = configDir ?? Path.Combine(AppPaths.ConfigDirectory, "AiSpeech");
        _dataDir = dataDir ?? Path.Combine(AppPaths.ResolveDataDirectory(), "AiSpeech");
        _log = log;
        EnsureDefaultFiles();
        ReloadAll(force: true);
        StartWatchers();
    }

    /// <summary>进程内共享实例（配置目录跟随 AppPaths）。</summary>
    public static AiPromptStore Shared
    {
        get
        {
            lock (GlobalLock)
            {
                return _shared ??= new AiPromptStore();
            }
        }
    }

    public static void EnsureDefaults()
    {
        try
        {
            Shared.EnsureDefaultFiles();
        }
        catch
        {
            // ignore
        }
    }

    public DateTime GetLoadedAt()
    {
        lock (_gate) return _loadedAtUtc;
    }

    public string GetVersion()
    {
        lock (_gate) return _version;
    }

    public string GetPersonality() => Get(PromptKind.Personality);
    public string GetDanmaku() => Get(PromptKind.Danmaku);
    public string GetGift() => Get(PromptKind.Gift);
    public string GetWelcome() => Get(PromptKind.Welcome);
    public string GetSummary() => Get(PromptKind.Summary);
    public string GetLike() => Get(PromptKind.Like);
    public string GetSongRequest() => Get(PromptKind.SongRequest);

    public string Get(PromptKind kind)
    {
        MaybeRefreshFromDisk(kind);
        lock (_gate)
        {
            return _cache.TryGetValue(kind, out var snap) ? snap.Text : GetBuiltInDefault(kind);
        }
    }

    public void SavePrompt(PromptKind kind, string text)
    {
        text ??= "";
        Directory.CreateDirectory(_configDir);
        var path = Path.Combine(_configDir, FileName(kind));
        AtomicWrite(path, text.Replace("\r\n", "\n").TrimEnd() + "\n");

        // 同步旧人格文件，便于兼容
        if (kind == PromptKind.Personality)
        {
            try
            {
                var legacy = Path.Combine(AppPaths.ConfigDirectory, AiPersonalityLoader.FileName);
                Directory.CreateDirectory(AppPaths.ConfigDirectory);
                AtomicWrite(legacy, text.Replace("\r\n", "\n").TrimEnd() + "\n");
            }
            catch
            {
                // ignore
            }
        }

        ReloadKind(kind, force: true);
    }

    public void EnsureDefaultFiles()
    {
        Directory.CreateDirectory(_configDir);
        foreach (PromptKind kind in Enum.GetValues<PromptKind>())
        {
            var path = Path.Combine(_configDir, FileName(kind));
            if (!File.Exists(path))
            {
                try
                {
                    File.WriteAllText(path, GetBuiltInDefault(kind).TrimEnd() + "\n");
                }
                catch
                {
                    // ignore
                }
            }
        }

        // 兼容：同步 ai_personality.txt
        try
        {
            var legacy = Path.Combine(AppPaths.ConfigDirectory, AiPersonalityLoader.FileName);
            if (!File.Exists(legacy))
            {
                Directory.CreateDirectory(AppPaths.ConfigDirectory);
                File.WriteAllText(legacy, GetBuiltInDefault(PromptKind.Personality).TrimEnd() + "\n");
            }
        }
        catch
        {
            // ignore
        }
    }

    private void StartWatchers()
    {
        TryWatch(_configDir);
        TryWatch(_dataDir);
    }

    private void TryWatch(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var w = new FileSystemWatcher(dir)
            {
                Filter = "*.txt",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            w.Changed += OnFsEvent;
            w.Created += OnFsEvent;
            w.Renamed += OnFsEvent;
            _watchers.Add(w);
        }
        catch
        {
            // ignore watcher failures
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        lock (_debounceLock)
        {
            try { _debounceCts?.Cancel(); } catch { /* ignore */ }
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300, token);
                    ReloadAll(force: true);
                }
                catch (OperationCanceledException)
                {
                    // debounce
                }
            }, token);
        }
    }

    private void MaybeRefreshFromDisk(PromptKind kind)
    {
        try
        {
            var path = ResolvePath(kind);
            if (path == null || !File.Exists(path))
            {
                return;
            }

            var mtime = File.GetLastWriteTimeUtc(path);
            lock (_gate)
            {
                if (_cache.TryGetValue(kind, out var snap) && snap.MtimeUtc == mtime && snap.Path == path)
                {
                    return;
                }
            }

            ReloadKind(kind, force: false);
        }
        catch
        {
            // ignore
        }
    }

    private void ReloadAll(bool force)
    {
        foreach (PromptKind kind in Enum.GetValues<PromptKind>())
        {
            ReloadKind(kind, force);
        }

        lock (_gate)
        {
            _loadedAtUtc = DateTime.UtcNow;
            _version = ComputeCombinedVersion();
        }
    }

    private void ReloadKind(PromptKind kind, bool force)
    {
        string? previousText = null;
        lock (_gate)
        {
            if (_cache.TryGetValue(kind, out var existing) && !string.IsNullOrWhiteSpace(existing.Text))
            {
                previousText = existing.Text;
            }
        }

        try
        {
            var path = ResolvePath(kind);
            string text = "";
            DateTime mtime = DateTime.MinValue;
            long length = -1;
            if (path != null && File.Exists(path))
            {
                // 稳定读：重试 + 连续两次内容/长度一致，避免编辑器写一半
                if (!TryReadStable(path, out text, out mtime, out length))
                {
                    if (!string.IsNullOrWhiteSpace(previousText))
                    {
                        _log?.Invoke($"AI_PROMPT_KEEP_OLD type={kind} reason=unstable_read path={Path.GetFileName(path)}");
                        return;
                    }
                }
            }

            text = (text ?? "").Trim();
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    // 永不覆盖有效缓存为空读
                    if (_cache.TryGetValue(kind, out var existing) && !string.IsNullOrWhiteSpace(existing.Text))
                    {
                        _log?.Invoke($"AI_PROMPT_KEEP_OLD type={kind} reason=empty_read");
                        return;
                    }

                    text = GetBuiltInDefault(kind).Trim();
                    path ??= Path.Combine(_configDir, FileName(kind));
                }

                // 半文件启发式：新内容明显短于旧版且旧版非空 → 保留旧版
                if (!string.IsNullOrWhiteSpace(previousText)
                    && previousText.Length >= 40
                    && text.Length < previousText.Length / 3
                    && length >= 0
                    && length < previousText.Length / 3)
                {
                    _log?.Invoke(
                        $"AI_PROMPT_KEEP_OLD type={kind} reason=truncated_suspect old_len={previousText.Length} new_len={text.Length}");
                    return;
                }

                var ver = ShortHash(text);
                _cache[kind] = new PromptSnapshot
                {
                    Text = text,
                    Version = ver,
                    MtimeUtc = mtime == DateTime.MinValue ? DateTime.UtcNow : mtime,
                    Path = path ?? ""
                };
                _loadedAtUtc = DateTime.UtcNow;
                _version = ComputeCombinedVersion();
            }

            _log?.Invoke($"AI_PROMPT_RELOAD type={kind} version={ShortHash(text)} mtime={mtime:O}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"AI_PROMPT_RELOAD type={kind} error={ex.Message}");
            if (!string.IsNullOrWhiteSpace(previousText))
            {
                _log?.Invoke($"AI_PROMPT_KEEP_OLD type={kind} reason=exception");
                return;
            }

            if (force)
            {
                lock (_gate)
                {
                    if (!_cache.ContainsKey(kind) || string.IsNullOrWhiteSpace(_cache[kind].Text))
                    {
                        var fallback = GetBuiltInDefault(kind).Trim();
                        _cache[kind] = new PromptSnapshot
                        {
                            Text = fallback,
                            Version = ShortHash(fallback),
                            MtimeUtc = DateTime.UtcNow,
                            Path = ""
                        };
                    }
                }
            }
        }
    }

    /// <summary>连续两次读到相同内容才接受；IO 失败时返回 false。</summary>
    private static bool TryReadStable(string path, out string text, out DateTime mtime, out long length)
    {
        text = "";
        mtime = DateTime.MinValue;
        length = -1;
        string? last = null;
        for (var i = 0; i < 4; i++)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                length = bytes.LongLength;
                mtime = File.GetLastWriteTimeUtc(path);
                var current = Encoding.UTF8.GetString(bytes);
                if (last != null && string.Equals(last, current, StringComparison.Ordinal))
                {
                    text = current;
                    return true;
                }

                last = current;
                Thread.Sleep(40);
            }
            catch (IOException) when (i < 3)
            {
                Thread.Sleep(40);
            }
            catch
            {
                return false;
            }
        }

        // 最后一次读到的内容作为兜底（已尽量稳定）
        if (last != null)
        {
            text = last;
            return true;
        }

        return false;
    }

    private string? ResolvePath(PromptKind kind)
    {
        var name = FileName(kind);
        var dataPath = Path.Combine(_dataDir, name);
        if (File.Exists(dataPath))
        {
            return dataPath;
        }

        var cfgPath = Path.Combine(_configDir, name);
        if (File.Exists(cfgPath))
        {
            return cfgPath;
        }

        // 人格兼容旧路径
        if (kind == PromptKind.Personality)
        {
            var legacyData = Path.Combine(AppPaths.ResolveDataDirectory(), AiPersonalityLoader.FileName);
            if (File.Exists(legacyData))
            {
                return legacyData;
            }

            var legacyCfg = Path.Combine(AppPaths.ConfigDirectory, AiPersonalityLoader.FileName);
            if (File.Exists(legacyCfg))
            {
                return legacyCfg;
            }
        }

        return File.Exists(cfgPath) ? cfgPath : null;
    }

    private string ComputeCombinedVersion()
    {
        var sb = new StringBuilder();
        foreach (PromptKind kind in Enum.GetValues<PromptKind>())
        {
            if (_cache.TryGetValue(kind, out var snap))
            {
                sb.Append(kind).Append(':').Append(snap.Version).Append(';');
            }
        }

        return ShortHash(sb.ToString());
    }

    public static string FileName(PromptKind kind) => kind switch
    {
        PromptKind.Personality => "personality.txt",
        PromptKind.Danmaku => "danmaku_prompt.txt",
        PromptKind.Gift => "gift_prompt.txt",
        PromptKind.Welcome => "welcome_prompt.txt",
        PromptKind.Summary => "summary_prompt.txt",
        PromptKind.Like => "like_prompt.txt",
        PromptKind.SongRequest => "song_request_prompt.txt",
        _ => "personality.txt"
    };

    public static string GetBuiltInDefault(PromptKind kind) => kind switch
    {
        PromptKind.Personality => AiPersonalityLoader.DefaultPersonalityText,
        PromptKind.Danmaku =>
            """
            【弹幕回复任务】
            根据观众弹幕，用主播口吻口头回复一句。
            只输出最终要朗读的话，10～40字，不要分析过程。
            """.Trim() + "\n",
        PromptKind.Gift =>
            """
            【礼物感谢任务】
            用主播口吻口头感谢礼物，提到昵称和礼物名，15～40字。
            """.Trim() + "\n",
        PromptKind.Welcome =>
            """
            【进房欢迎任务】
            用主播口吻欢迎进房观众，10～30字，最多点三个昵称。
            """.Trim() + "\n",
        PromptKind.Summary =>
            """
            【直播间短总结任务】
            根据最近弹幕氛围说一句短总结，20～45字。
            """.Trim() + "\n",
        PromptKind.Like =>
            """
            【点赞感谢任务】
            口头感谢点赞，10～25字。
            """.Trim() + "\n",
        PromptKind.SongRequest =>
            """
            【点歌成功播报任务】
            用主播口吻告知观众点歌已排上，提到昵称和歌名，可带前方排队数，15～40字。
            只输出最终要朗读的话，不要分析过程。
            """.Trim() + "\n",
        _ => AiPersonalityLoader.DefaultPersonalityText
    };

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        // 尽量用替换，减少读者看到半文件的窗口
        try
        {
            File.Replace(tmp, path, null);
        }
        catch (IOException)
        {
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
        catch (PlatformNotSupportedException)
        {
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static string ShortHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(bytes.AsSpan(0, 4)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* ignore */ }
        }

        _watchers.Clear();
        try { _debounceCts?.Cancel(); _debounceCts?.Dispose(); } catch { /* ignore */ }

        lock (GlobalLock)
        {
            if (ReferenceEquals(_shared, this))
            {
                _shared = null;
            }
        }
    }
}
