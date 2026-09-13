using LiveAssistant.Config;
using LiveAssistant.Models;

namespace LiveAssistant.Services;

/// <summary>
/// 播放模式配置与命令门面（实际操作由 PlaybackCommandQueue 串行执行）。
/// </summary>
public sealed class PlaybackEngine
{
    private readonly ConfigManager _config;
    private readonly PlaybackCommandQueue _commands;
    private readonly SystemMessageService _system;

    public PlaybackEngine(ConfigManager config, PlaybackCommandQueue commands, SystemMessageService system)
    {
        _config = config;
        _commands = commands;
        _system = system;
    }

    public PlaybackMode Mode => _config.Settings.Playback.Mode;

    public void SetMode(PlaybackMode mode)
    {
        _config.Settings.Playback.Mode = mode;
        _config.Save();
        _system.Add($"播放模式已切换: {mode}");
    }

    public Task EnsurePlayingAsync() => _commands.EnqueueEnsurePlayingAsync();

    public Task SkipAsync() => _commands.EnqueueSkipAsync();

    public void Pause() => _commands.EnqueuePause();

    public void Resume() => _commands.EnqueueResume();

    public void Stop() => _commands.EnqueueStop();
}
